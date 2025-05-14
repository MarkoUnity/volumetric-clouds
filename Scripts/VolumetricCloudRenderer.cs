using System;
using UnityEngine;

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
        public int downSample = 1;
        public bool allowCloudFrontObject;
        
        [Header("Adaptive Sampling")]
        [Range(8, 32)]
        public int minSampleCount = 16;
        [Range(32, 128)]
        public int maxSampleCount = 64;
        [Range(0.1f, 2.0f)]
        public float sampleDistanceScale = 1.0f;
        [Range(0.1f, 2.0f)]
        public float densitySampleScale = 1.0f;

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
        [SerializeField]
        private bool useHierarchicalHeightMap;
        private Vector2Int hiHeightLevelRange = new Vector2Int(0, 9);
        private Vector2Int heightLutTextureSize = new Vector2Int(512, 512);
        private RenderTexture heightLutTexture;
        private RenderTexture hiHeightTexture;
        private RenderTexture[] hiHeightTempTextures;

        [Header("Shader references(DONT EDIT)")]
        public Shader cloudShader;
        public ComputeShader heightPreprocessShader;
        public Shader cloudHeightProcessShader;

        [Header("Debug")]
        public bool showSampleCount = false;

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
        }

        private void Start() {
            EnsureMaterial(true);
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

            if (useHierarchicalHeightMap) {
                GenerateHierarchicalHeightMap();
                mat.EnableKeyword("USE_HI_HEIGHT");
                mat.SetTexture("_HiHeightMap", this.hiHeightTexture);
                mat.SetInt("_HeightMapSize", this.hiHeightTexture.width);
                mat.SetInt("_HiHeightMinLevel", this.hiHeightLevelRange.x);
                mat.SetInt("_HiHeightMaxLevel", this.hiHeightLevelRange.y);
            } else {
                mat.DisableKeyword("USE_HI_HEIGHT");
            }

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
                Graphics.Blit(source, destination, mat, 2);
            }

            prevV = mcam.worldToCameraMatrix;
            firstFrame = false;
        }
    }
}