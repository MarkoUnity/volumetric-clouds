using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VolumetricClouds {

    /// <summary>
    /// Generate halton sequence.
    /// code from unity post-processing stack.
    /// </summary>
    public class HaltonSequence {
        public int radix = 3;
        private int storedIndex = 0;
        public float Get() {
            float result = 0f;
            float fraction = 1f / (float)radix;
            int index = storedIndex;
            while (index > 0) {
                result += (float)(index % radix) * fraction;

                index /= radix;
                fraction /= (float)radix;
            }
            storedIndex++;
            return result;
        }
    }

    /// <summary>
    /// Cloud renderer post processing.
    /// </summary>
    [ImageEffectAllowedInSceneView]
    [ExecuteInEditMode,RequireComponent(typeof(Camera))]
    [ImageEffectOpaque]
    public class VolumetricCloudRenderer : EffectBase {
        [Header("Config")]
        public VolumetricCloudsConfiguration configuration;
        [Header("Render Settings")]
        [Range(0, 2)]
        public int downSample = 0;
        public bool allowCloudFrontObject;

        
        [Header("Adaptive Sampling")]
        [Range(8, 32)]
        public int minSampleCount = 32;
        [Range(32, 128)]
        public int maxSampleCount = 128;
        [Range(0.1f, 2.0f)]
        public float sampleDistanceScale = 2.0f;
        [Range(0.1f, 2.0f)]
        public float densitySampleScale = 1.0f;

        private RenderTexture cloudShadowTexture;
        private VolumetricCloudsConfiguration.ShadowQuality previousShadowQuality = VolumetricCloudsConfiguration.ShadowQuality.High;
        private Material cloudShadowMaterial;
        private Light directionalLight;

        private Material mat;
        private Material heightDownsampleMat;
        private RenderTexture[] fullBuffer = new RenderTexture[2];
        private int fullBufferIndex;
        private RenderTexture undersampleBuffer;
        private RenderTexture downsampledDepth;
        private Matrix4x4 prevV;
        private Camera mcam;
        private HaltonSequence sequence = new HaltonSequence() { radix = 3 };
        private int frameIndex = 0;
        private bool firstFrame = true;

        [Header("Hi-Height")]
        private Vector2Int hiHeightLevelRange = new Vector2Int(0, 9);
        private Vector2Int heightLutTextureSize = new Vector2Int(512, 512);
        private RenderTexture heightLutTexture;
        private RenderTexture hiHeightTexture;
        private RenderTexture[] hiHeightTempTextures;

        [Header("Shader references")]
        public Shader cloudShader;
        public ComputeShader heightPreprocessShader;
        public Shader cloudHeightProcessShader;
        public Shader cloudShadowShader;

        void EnsureMaterial(bool force = false) {
            if (cloudShader == null) {
                return;
            }

            if (mat == null || force) {
                if (mat != null) {
                    if (Application.isPlaying) {
                        Destroy(mat);
                    } else {
                        DestroyImmediate(mat);
                    }
                }
                mat = new Material(cloudShader);
            }

            if (cloudHeightProcessShader == null) {
                Debug.LogError("Cloud height process shader is not assigned in the inspector!");
                return;
            }

            if (heightDownsampleMat == null || force) {
                if (heightDownsampleMat != null) {
                    if (Application.isPlaying) {
                        Destroy(heightDownsampleMat);
                    } else {
                        DestroyImmediate(heightDownsampleMat);
                    }
                }
                heightDownsampleMat = new Material(cloudHeightProcessShader);
            }

            if (cloudShadowShader != null && (cloudShadowMaterial == null || force)) {
                if (cloudShadowMaterial != null) {
                    if (Application.isPlaying) {
                        Destroy(cloudShadowMaterial);
                    } else {
                        DestroyImmediate(cloudShadowMaterial);
                    }
                }
                cloudShadowMaterial = new Material(cloudShadowShader);
            }
        }

        private void ReleaseRenderTexture(ref RenderTexture rt) {
            if (rt != null) {
                rt.Release();
                if (Application.isPlaying) {
                    Destroy(rt);
                } else {
                    DestroyImmediate(rt);
                }
                rt = null;
            }
        }

        private void OnEnable() {
            EnsureMaterial(true);
            #if UNITY_EDITOR
            if (!Application.isPlaying) {
                // Initial immediate refresh
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                
                // Schedule additional refreshes with delays
                EditorApplication.delayCall += () => {
                    EditorApplication.QueuePlayerLoopUpdate();
                    SceneView.RepaintAll();
                    
                    // One more refresh after a short delay
                    EditorApplication.delayCall += () => {
                        EditorApplication.QueuePlayerLoopUpdate();
                        SceneView.RepaintAll();
                    };
                };
            }
            #endif
        }

        private void OnDisable() {
            // Release all render textures
            for (int i = 0; i < fullBuffer.Length; i++) {
                ReleaseRenderTexture(ref fullBuffer[i]);
            }
            ReleaseRenderTexture(ref undersampleBuffer);
            ReleaseRenderTexture(ref downsampledDepth);
            ReleaseRenderTexture(ref heightLutTexture);
            ReleaseRenderTexture(ref hiHeightTexture);
            ReleaseRenderTexture(ref cloudShadowTexture);
            if (hiHeightTempTextures != null) {
                for (int i = 0; i < hiHeightTempTextures.Length; i++) {
                    ReleaseRenderTexture(ref hiHeightTempTextures[i]);
                }
            }
        }

        private void OnDestroy() {
            OnDisable();
            if (mat != null) {
                if (Application.isPlaying) {
                    Destroy(mat);
                } else {
                    DestroyImmediate(mat);
                }
            }
            if (heightDownsampleMat != null) {
                if (Application.isPlaying) {
                    Destroy(heightDownsampleMat);
                } else {
                    DestroyImmediate(heightDownsampleMat);
                }
            }
            if (cloudShadowMaterial != null) {
                if (Application.isPlaying) {
                    Destroy(cloudShadowMaterial);
                } else {
                    DestroyImmediate(cloudShadowMaterial);
                }
            }

            // Clear the cookie when destroyed
            if (directionalLight != null) {
                directionalLight.cookie = null;
            }
        }

        private void Start() {
            EnsureMaterial(true);
            AssignMainLight();
            previousShadowQuality = configuration == null ? VolumetricCloudsConfiguration.ShadowQuality.High : configuration.shadowQuality;
        }

        private void AssignMainLight()
        {
            Light[] lights = FindObjectsOfType<Light>();
            
            if (lights == null || lights.Length == 0) {
                return;
            }

            foreach (Light light in lights) {
                if (light.type == LightType.Directional) {
                    directionalLight = light;
                    break;
                }
            }
        }

        private void GenerateHierarchicalHeightMap() {
            RenderTexture defaultTarget = RenderTexture.active;

            if (this.configuration.weatherTex.width != 512 || this.configuration.weatherTex.height != 512) {
                throw new UnityException("Hierarchical height map mode only supports weather tex of size 512*512!");
            }

            if (heightLutTexture == null || !heightLutTexture.IsCreated() || 
                heightLutTexture.width != heightLutTextureSize.x || 
                heightLutTexture.height != heightLutTextureSize.y) {
                ReleaseRenderTexture(ref heightLutTexture);
                heightLutTexture = new RenderTexture(heightLutTextureSize.x, heightLutTextureSize.y, 0, RenderTextureFormat.RFloat);
                heightLutTexture.enableRandomWrite = true;
                heightLutTexture.Create();
            }

            var kernal = heightPreprocessShader.FindKernel("CSMain");
            heightPreprocessShader.SetTexture(kernal, "heightDensityMap", configuration.heightDensityMap);
            heightPreprocessShader.SetTexture(kernal, "heightLutResult", this.heightLutTexture);
            heightPreprocessShader.Dispatch(kernal, heightLutTextureSize.x / 32, heightLutTextureSize.y / 32, 1);

            if (hiHeightTexture == null || !hiHeightTexture.IsCreated() || 
                hiHeightTexture.width != 512 || hiHeightTexture.height != 512) {
                ReleaseRenderTexture(ref hiHeightTexture);
                hiHeightTexture = new RenderTexture(512, 512, 0, RenderTextureFormat.RFloat);
                hiHeightTexture.enableRandomWrite = true;
                hiHeightTexture.useMipMap = true;
                hiHeightTexture.wrapMode = configuration.weatherTex.wrapMode;
                hiHeightTexture.Create();
            }

            if (hiHeightTempTextures == null || hiHeightTempTextures.Length != 10) {
                if (hiHeightTempTextures != null) {
                    for (int i = 0; i < hiHeightTempTextures.Length; i++) {
                        ReleaseRenderTexture(ref hiHeightTempTextures[i]);
                    }
                }
                hiHeightTempTextures = new RenderTexture[10];
            }

            for (int i = 0; i <= 9; i++) {
                if (hiHeightTempTextures[i] == null || !hiHeightTempTextures[i].IsCreated() || 
                    hiHeightTempTextures[i].width != (512 >> i) || 
                    hiHeightTempTextures[i].height != (512 >> i)) {
                    ReleaseRenderTexture(ref hiHeightTempTextures[i]);
                    hiHeightTempTextures[i] = new RenderTexture(512 >> i, 512 >> i, 0, RenderTextureFormat.RFloat);
                    hiHeightTempTextures[i].Create();
                }
            }

            this.heightDownsampleMat.SetTexture("_WeatherTex", this.configuration.weatherTex);
            this.heightDownsampleMat.SetTexture("_HeightLut", this.heightLutTexture);
            Graphics.Blit(null, hiHeightTempTextures[0], this.heightDownsampleMat, 0);
            Graphics.CopyTexture(hiHeightTempTextures[0], 0, 0, hiHeightTexture, 0, 0);

            for (int i = 1; i <= Mathf.Min(9, hiHeightLevelRange.y); i++) {
                Graphics.Blit(hiHeightTempTextures[i - 1], hiHeightTempTextures[i], this.heightDownsampleMat, 1);
                Graphics.CopyTexture(hiHeightTempTextures[i], 0, 0, hiHeightTexture, 0, i);
            }
            RenderTexture.active = defaultTarget;
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination) {
            if (this.configuration == null || cloudShader == null) {
                Graphics.Blit(source, destination);
                return;
            }

            mcam = GetComponent<Camera>();
            if (mcam == null) {
                Graphics.Blit(source, destination);
                return;
            }

            var width = mcam.pixelWidth >> downSample;
            var height = mcam.pixelHeight >> downSample;

            if (width <= 0 || height <= 0) {
                Graphics.Blit(source, destination);
                return;
            }

            this.EnsureMaterial();
            this.configuration.ApplyToMaterial(this.mat);

            // Create or update render textures
            for (int i = 0; i < fullBuffer.Length; i++) {
                if (fullBuffer[i] == null || !fullBuffer[i].IsCreated() || 
                    fullBuffer[i].width != width || fullBuffer[i].height != height) {
                    ReleaseRenderTexture(ref fullBuffer[i]);
                    fullBuffer[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
                    fullBuffer[i].Create();
                }
            }

            // Create or update top-down view texture
            if (cloudShadowTexture == null || !cloudShadowTexture.IsCreated() || 
                cloudShadowTexture.width != 2048 || cloudShadowTexture.height != 2048) {
                ReleaseRenderTexture(ref cloudShadowTexture);
                cloudShadowTexture = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGB32);
                cloudShadowTexture.enableRandomWrite = true;
                cloudShadowTexture.useMipMap = true;
                cloudShadowTexture.autoGenerateMips = true;
                cloudShadowTexture.Create();
                cloudShadowTexture.filterMode = FilterMode.Bilinear;
                cloudShadowTexture.wrapMode = TextureWrapMode.Repeat;
            }

            if (undersampleBuffer == null || !undersampleBuffer.IsCreated() || 
                undersampleBuffer.width != width || undersampleBuffer.height != height) {
                ReleaseRenderTexture(ref undersampleBuffer);
                undersampleBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
                undersampleBuffer.Create();
            }

            if (downsampledDepth == null || !downsampledDepth.IsCreated() || 
                downsampledDepth.width != width || downsampledDepth.height != height) {
                ReleaseRenderTexture(ref downsampledDepth);
                downsampledDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat);
                downsampledDepth.Create();
            }

            frameIndex = (frameIndex + 1)% 16;
            fullBufferIndex = (fullBufferIndex + 1) % 2;

            GenerateHierarchicalHeightMap();
            mat.EnableKeyword("USE_HI_HEIGHT");
            mat.SetTexture("_HiHeightMap", this.hiHeightTexture);
            mat.SetInt("_HeightMapSize", this.hiHeightTexture.width);
            mat.SetInt("_HiHeightMinLevel", this.hiHeightLevelRange.x);
            mat.SetInt("_HiHeightMaxLevel", this.hiHeightLevelRange.y);

            // Remove quality-based sampling
            mat.DisableKeyword("HIGH_QUALITY");
            mat.DisableKeyword("MEDIUM_QUALITY");
            mat.DisableKeyword("LOW_QUALITY");

            if (allowCloudFrontObject) {
                mat.EnableKeyword("ALLOW_CLOUD_FRONT_OBJECT");
            } else {
                mat.DisableKeyword("ALLOW_CLOUD_FRONT_OBJECT");
            }

            mat.SetVector("_ProjectionExtents", mcam.GetProjectionExtents());
            mat.SetFloat("_RaymarchOffset", sequence.Get());
            mat.SetVector("_TexelSize", undersampleBuffer.texelSize);

            // Set adaptive sampling parameters
            mat.SetFloat("_CloudMinSamples", minSampleCount);
            mat.SetFloat("_CloudMaxSamples", maxSampleCount);
            mat.SetFloat("_CloudDistanceScale", sampleDistanceScale);
            mat.SetFloat("_CloudDensityScale", densitySampleScale);

            if (downSample > 0) {
                Graphics.Blit(null, downsampledDepth, mat, 3);
            } else {
                Graphics.Blit(null, downsampledDepth, mat, 4);
            }
            mat.SetTexture("_DownsampledDepth", downsampledDepth);

            Graphics.Blit(null, undersampleBuffer, mat, 0);

            mat.SetTexture("_UndersampleCloudTex", undersampleBuffer);
            mat.SetMatrix("_PrevVP", GL.GetGPUProjectionMatrix(mcam.projectionMatrix,false) * prevV);
            mat.SetVector("_ProjectionExtents", mcam.GetProjectionExtents());

            if (firstFrame) {
                Graphics.Blit(undersampleBuffer, fullBuffer[fullBufferIndex]);
            } else {
                Graphics.Blit(fullBuffer[fullBufferIndex], fullBuffer[fullBufferIndex ^ 1], mat, 1);
            }

            mat.SetTexture("_CloudTex", fullBuffer[fullBufferIndex ^ 1]);


            // Create a temporary buffer if source and destination are the same
            RenderTexture tempBuffer = null;
            if (source == destination) {
                tempBuffer = RenderTexture.GetTemporary(destination.width, destination.height, 0, destination.format);
                Graphics.Blit(source, tempBuffer, mat, 2);
                Graphics.Blit(tempBuffer, destination);
                RenderTexture.ReleaseTemporary(tempBuffer);
            } else {
                RenderShadows(); 
                Graphics.Blit(source, destination, mat, 2);
            }

            prevV = mcam.worldToCameraMatrix;
            firstFrame = false;
        }

        private void RenderShadows(){
            
            // Handle quality transitions
            if (configuration.shadowQuality != previousShadowQuality) {
                if (configuration.shadowQuality == VolumetricCloudsConfiguration.ShadowQuality.Off) {
                    // Transitioning to Off - release texture
                    if (cloudShadowTexture != null && cloudShadowTexture.IsCreated()) {
                        cloudShadowTexture.Release();
                    }
                    if (directionalLight != null) {
                        directionalLight.cookie = null;
                    }
                } else if (previousShadowQuality == VolumetricCloudsConfiguration.ShadowQuality.Off) {
                    // Transitioning from Off to Low/High - recreate texture
                    if (cloudShadowTexture != null) {
                        cloudShadowTexture.Create();
                    }
                }
                previousShadowQuality = configuration.shadowQuality;
            }

            if (configuration.shadowQuality == VolumetricCloudsConfiguration.ShadowQuality.Off) {
                return;
            }

            if (cloudShadowShader != null && cloudShadowMaterial != null) {
                cloudShadowMaterial.SetVector("_WindDirection", new Vector4(
                    configuration.windDirection.x,
                    configuration.windDirection.y,
                    configuration.windSpeed,
                    -configuration.windSpeed
                ));
                cloudShadowMaterial.SetTexture("_WeatherTex", configuration.weatherTex);
                cloudShadowMaterial.SetFloat("_WeatherTexSize", configuration.weatherTexSize);
                cloudShadowMaterial.SetTexture("_BaseTex", configuration.baseTexture);
                cloudShadowMaterial.SetFloat("_BaseTile", configuration.baseTile);
                cloudShadowMaterial.SetFloat("_CloudStartHeight", configuration.cloudHeightRange.x);
                cloudShadowMaterial.SetFloat("_CloudEndHeight", configuration.cloudHeightRange.y);
                cloudShadowMaterial.SetFloat("_CloudOverallDensity", configuration.overallDensity);
                cloudShadowMaterial.SetFloat("_CloudCoverageModifier", configuration.cloudCoverageModifier);
                cloudShadowMaterial.SetFloat("_ShadowIntensity", configuration.shadowIntensity);
                cloudShadowMaterial.SetFloat("_BlurSize", configuration.blurSize); 

                // --- Blur pipeline ---
                RenderTexture tempRT1 = RenderTexture.GetTemporary(2048, 2048, 0, RenderTextureFormat.ARGB32);
                RenderTexture tempRT2 = RenderTexture.GetTemporary(2048, 2048, 0, RenderTextureFormat.ARGB32);

                // 1. Render clouds to tempRT1 (first pass)
                Graphics.Blit(null, tempRT1, cloudShadowMaterial, 0); // Pass 0: cloud alpha

                if (configuration.shadowQuality == VolumetricCloudsConfiguration.ShadowQuality.Low) {
                    // Low quality: Single pass blur
                    cloudShadowMaterial.SetFloat("_BlurDirection", 0.0f);
                    Graphics.Blit(tempRT1, cloudShadowTexture, cloudShadowMaterial, 1);
                } else {
                    // High quality: Full two-pass blur
                    // 2. Horizontal blur to tempRT2
                    cloudShadowMaterial.SetFloat("_BlurDirection", 0.0f);
                    Graphics.Blit(tempRT1, tempRT2, cloudShadowMaterial, 1);
 
                    // 3. Vertical blur to cloudShadowTexture
                    cloudShadowMaterial.SetFloat("_BlurDirection", 1.0f);
                    Graphics.Blit(tempRT2, cloudShadowTexture, cloudShadowMaterial, 1);
                }

                RenderTexture.ReleaseTemporary(tempRT1);
                RenderTexture.ReleaseTemporary(tempRT2);
                // --- End blur pipeline ---

                //assign as cookie texture on the main light
                if (directionalLight != null) {
                    directionalLight.cookie = cloudShadowTexture;
                    directionalLight.cookieSize = configuration.weatherTexSize / 10f;
                }
            }
        }

        // Public property to access the top-down view texture
        public RenderTexture CloudShadowTexture {
            get { return cloudShadowTexture; }
        }
    }

    #if UNITY_EDITOR
    [CustomEditor(typeof(VolumetricCloudRenderer))]
    public class VolumetricCloudRendererEditor : Editor {
        public override void OnInspectorGUI() {
            var renderer = (VolumetricCloudRenderer)target;
            
            if (renderer.configuration == null) {
                EditorGUILayout.HelpBox("No cloud configuration assigned. Create a default configuration?", MessageType.Warning);
                if (GUILayout.Button("Create Default Config")) {
                    CreateDefaultConfig(renderer);
                }
                EditorGUILayout.Space();
            }
            
            DrawDefaultInspector();
        }

        private void CreateDefaultConfig(VolumetricCloudRenderer renderer) {
            // Find the default config asset
            string[] guids = AssetDatabase.FindAssets("t:VolumetricCloudsConfiguration");
            VolumetricCloudsConfiguration defaultConfig = null;
            
            foreach (string guid in guids) {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<VolumetricCloudsConfiguration>(path);
                if (config != null) {
                    defaultConfig = config;
                    break;
                }
            }

            if (defaultConfig == null) {
                EditorUtility.DisplayDialog("Error", "Could not find default cloud configuration!", "OK");
                return;
            }

            // Create the Config directory if it doesn't exist
            if (!AssetDatabase.IsValidFolder("Assets/Config")) {
                AssetDatabase.CreateFolder("Assets", "Config");
            }

            // Create a unique name for the new config
            string newPath = "Assets/Config/CloudsConfig.asset";
            int counter = 1;
            while (AssetDatabase.LoadAssetAtPath<VolumetricCloudsConfiguration>(newPath) != null) {
                newPath = $"Assets/Config/CloudsConfig_{counter}.asset";
                counter++;
            }

            // Create a copy of the default config
            var newConfig = Instantiate(defaultConfig);
            AssetDatabase.CreateAsset(newConfig, newPath);
            AssetDatabase.SaveAssets();

            // Assign the new configuration to the renderer
            renderer.configuration = newConfig;
            EditorUtility.SetDirty(renderer);
            
            // Select the new asset in the Project window
            Selection.activeObject = newConfig;
        }
    }
    #endif
}