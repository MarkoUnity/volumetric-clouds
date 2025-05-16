Shader "Custom/CloudShadows"
{
    Properties
    {
        _WeatherTex("WeatherTex", 2D) = "white" {}
        _MainTex("MainTex", 2D) = "white" {}
        _WeatherTexSize("WeatherTexSize", float) = 50000
        _BaseTex("BaseTex", 3D) = "white" {}
        _BaseTile("BaseTile", float) = 3.0
        _CloudStartHeight("CloudStartHeight", float) = 2000
        _CloudEndHeight("CloudEndHeight", float) = 8000
        _CloudOverallDensity("CloudOverallDensity", float) = 0.1
        _CloudCoverageModifier("CloudCoverageModifier", float) = 1.0
        _ShadowIntensity("Shadow Intensity", Range(0, 1)) = 0.8
        _WindDirection("Wind Direction", Vector) = (1,1,0,0)
        _BlurSize("Blur Size", float) = 1.0
        _BlurDirection("Blur Direction", float) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _WeatherTex;
            float _WeatherTexSize;
            sampler3D _BaseTex;
            float _BaseTile;
            float _CloudStartHeight;
            float _CloudEndHeight;
            float _CloudOverallDensity;
            float _CloudCoverageModifier;
            float _ShadowIntensity;
            float4 _WindDirection;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // Convert UV to world position
                float2 uv = i.uv;
                float2 worldPosXZ = uv * _WeatherTexSize - _WeatherTexSize * 0.5;
                float worldPosY = 0.5 * (_CloudEndHeight + _CloudStartHeight); // Use mid-cloud height for 2D slice
                float3 worldPos = float3(worldPosXZ.x, worldPosY, worldPosXZ.y);

                // --- ApplyWind logic from main cloud shader ---
                float heightPercent = saturate((worldPos.y - _CloudStartHeight) / (_CloudEndHeight - _CloudStartHeight));
                worldPos.xz += (heightPercent) * _WindDirection.xy;
                worldPos.xz += (_WindDirection.xy + float2(0.0, 0.1)) * _Time.y * _WindDirection.z;
                // ------------------------------------------------

                // Sample weather texture for coverage
                float2 weatherUV = (worldPos.xz + _WeatherTexSize * 0.5) / _WeatherTexSize;
                float4 weather = tex2D(_WeatherTex, weatherUV);
                // Apply coverage and density
                float density = weather.g * (_CloudCoverageModifier) * 20;
                //density = saturate((density - 0.5) * (1 + _CloudCoverageModifier * 10) + 1);

                float finalDensity = 1 - (density * (1.0 - pow(1.0 - _CloudOverallDensity, 5.0)));

                // Apply contrast using _ShadowIntensity
                finalDensity = lerp(1.0, saturate((finalDensity - 0.5) * 4.0 + 1), _ShadowIntensity);

                return float4(finalDensity, finalDensity, finalDensity, finalDensity);
            }
            ENDCG
        }

        // Second pass: Separable Gaussian blur (horizontal or vertical)
        Pass
        {
            Name "GaussianBlur"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag_blur
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurSize;
            float _BlurDirection; // 0 = horizontal, 1 = vertical

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag_blur(v2f i) : SV_Target {
                float2 dir = (_BlurDirection < 0.5) ? float2(_BlurSize * _MainTex_TexelSize.x, 0) : float2(0, _BlurSize * _MainTex_TexelSize.y);

                //Gaussian weights
                float weights[13] = {
                    0.002216, 0.008764, 0.026995, 0.064759, 0.120985, 0.176033,
                    0.199471, 0.176033, 0.120985, 0.064759, 0.026995, 0.008764, 0.002216
                };
                float alpha = 0;
                float total = 0;

                float2 jitter = float2(
                    frac(sin(dot(i.uv, float2(12.9898,78.233))) * 43758.5453),
                    frac(sin(dot(i.uv, float2(39.3468,11.135))) * 24634.6345)
                );
                jitter = (jitter - 0.5) * _MainTex_TexelSize.xy * 1.5;

                [unroll]
                for (int k = -6; k <= 6; ++k) {
                    float2 offset = dir * k + jitter;
                    float weight = weights[k + 6];
                    alpha += tex2D(_MainTex, i.uv + offset).a * weight;
                    total += weight;
                }
                alpha /= total;
                return float4(alpha, alpha, alpha, alpha);
            }
            ENDCG
        }
    }
} 