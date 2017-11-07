using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;

namespace UnityEngine.Experimental.Rendering.LightweightPipeline
{
    [Serializable]
    public class ShadowSettings
    {
        public bool     enabled;
        public int      shadowAtlasWidth;
        public int      shadowAtlasHeight;

        public float    maxShadowDistance;
        public int      directionalLightCascadeCount;
        public Vector3  directionalLightCascades;
        public float    directionalLightNearPlaneOffset;

        static ShadowSettings defaultShadowSettings = null;

        public static ShadowSettings Default
        {
            get
            {
                if (defaultShadowSettings == null)
                {
                    defaultShadowSettings = new ShadowSettings();
                    defaultShadowSettings.enabled = true;
                    defaultShadowSettings.shadowAtlasHeight = defaultShadowSettings.shadowAtlasWidth = 4096;
                    defaultShadowSettings.directionalLightCascadeCount = 1;
                    defaultShadowSettings.directionalLightCascades = new Vector3(0.05F, 0.2F, 0.3F);
                    defaultShadowSettings.directionalLightCascadeCount = 4;
                    defaultShadowSettings.directionalLightNearPlaneOffset = 5;
                    defaultShadowSettings.maxShadowDistance = 1000.0F;
                }
                return defaultShadowSettings;
            }
        }
    }

    public struct ShadowSliceData
    {
        public Matrix4x4    shadowTransform;
        public int          atlasX;
        public int          atlasY;
        public int          shadowResolution;
    }

    public struct LightData
    {
        public int pixelAdditionalLightsCount;
        public int totalAdditionalLightsCount;
        public int mainLightIndex;
        public bool shadowsRendered;
    }

    public static class CameraRenderTargetID
    {
        // Camera color target uses to render opaques.
        // It's the same as final color except when BeforeOpaque custom PostFX is enabled
        public static int opaqueColor;

        // Camera color target. Not used when camera is rendering to backbuffer or camera
        // is rendering to a texture (offscreen camera)
        public static int finalColor;

        // Camera depth target. Only used when post processing or soft particles are enabled.
        public static int depth;

        // If soft particles are enabled
        public static int depthCopy;
    }

    public class LightweightPipeline : RenderPipeline
    {
        private readonly LightweightPipelineAsset m_Asset;

        // Maximum amount of visible lights the shader can process. This controls the constant global light buffer size.
        // It must match the MAX_VISIBLE_LIGHTS in LightweightCore.cginc
        private static readonly int kMaxVisibleAdditionalLights = 16;

        // Lights are culled per-object. This holds the maximum amount of additional lights that can shade each object.
        // The engine fills in the lights indices per-object in unity4_LightIndices0 and unity_4LightIndices1
        private static readonly int kMaxPerObjectAdditionalLights = 8;

        private bool m_IsOffscreenCamera;

        private Vector4[] m_LightPositions = new Vector4[kMaxVisibleAdditionalLights];
        private Vector4[] m_LightColors = new Vector4[kMaxVisibleAdditionalLights];
        private Vector4[] m_LightDistanceAttenuations = new Vector4[kMaxVisibleAdditionalLights];
        private Vector4[] m_LightSpotDirections = new Vector4[kMaxVisibleAdditionalLights];
        private Vector4[] m_LightSpotAttenuations = new Vector4[kMaxVisibleAdditionalLights];

        private Camera m_CurrCamera = null;

        private static readonly int kMaxCascades = 4;
        private int m_ShadowCasterCascadesCount = kMaxCascades;
        private int m_ShadowMapRTID;
        private RenderTargetIdentifier m_CurrCameraColorRT;
        private RenderTargetIdentifier m_ShadowMapRT;
        //private RenderTargetIdentifier m_OpaqueColorRT;
        private RenderTargetIdentifier m_FinalColorRT;
        private RenderTargetIdentifier m_OpaqueDepthRT;
        private RenderTargetIdentifier m_FinalDepthRT;

        private bool m_IntermediateTextureArray = false;

        private const int kShadowDepthBufferBits = 16;
        private const int kCameraDepthBufferBits = 32;
        private Vector4[] m_DirectionalShadowSplitDistances = new Vector4[kMaxCascades];

        private ShadowSettings m_ShadowSettings = ShadowSettings.Default;
        private ShadowSliceData[] m_ShadowSlices = new ShadowSliceData[kMaxCascades];

        private static readonly ShaderPassName m_LitPassName = new ShaderPassName("LightweightForward");
        private static readonly ShaderPassName m_UnlitPassName = new ShaderPassName("SRPDefaultUnlit");

        private RenderTextureFormat m_ColorFormat;
        private PostProcessRenderContext m_PostProcessRenderContext;
        private PostProcessLayer m_CameraPostProcessLayer;

        private CameraComparer m_CameraComparer = new CameraComparer();
        private LightComparer m_LightCompararer = new LightComparer();

        // Maps from sorted light indices to original unsorted. We need this for shadow rendering
        // and per-object light lists.
        private List<int> m_SortedLightIndexMap = new List<int>();

        private Mesh m_BlitQuad;
        private Material m_BlitMaterial;
        private Material m_CopyDepthMaterial;
        private int m_BlitTexID = Shader.PropertyToID("_BlitTex");

        private CopyTextureSupport m_CopyTextureSupport;

        public LightweightPipeline(LightweightPipelineAsset asset)
        {
            m_Asset = asset;

            BuildShadowSettings();

            PerFrameBuffer._GlossyEnvironmentColor = Shader.PropertyToID("_GlossyEnvironmentColor");

            // Lights are culled per-camera. Therefore we need to reset light buffers on each camera render
            PerCameraBuffer._MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            PerCameraBuffer._MainLightColor = Shader.PropertyToID("_MainLightColor");
            PerCameraBuffer._MainLightDistanceAttenuation = Shader.PropertyToID("_MainLightDistanceAttenuation");
            PerCameraBuffer._MainLightSpotDir = Shader.PropertyToID("_MainLightSpotDir");
            PerCameraBuffer._MainLightSpotAttenuation = Shader.PropertyToID("_MainLightSpotAttenuation");
            PerCameraBuffer._MainLightCookie = Shader.PropertyToID("_MainLightCookie");
            PerCameraBuffer._WorldToLight = Shader.PropertyToID("_WorldToLight");
            PerCameraBuffer._AdditionalLightCount = Shader.PropertyToID("_AdditionalLightCount");
            PerCameraBuffer._AdditionalLightPosition = Shader.PropertyToID("_AdditionalLightPosition");
            PerCameraBuffer._AdditionalLightColor = Shader.PropertyToID("_AdditionalLightColor");
            PerCameraBuffer._AdditionalLightDistanceAttenuation = Shader.PropertyToID("_AdditionalLightDistanceAttenuation");
            PerCameraBuffer._AdditionalLightSpotDir = Shader.PropertyToID("_AdditionalLightSpotDir");
            PerCameraBuffer._AdditionalLightSpotAttenuation = Shader.PropertyToID("_AdditionalLightSpotAttenuation");

            m_ShadowMapRTID = Shader.PropertyToID("_ShadowMap");

            CameraRenderTargetID.opaqueColor = Shader.PropertyToID("_CameraOpaqueColorRT");
            CameraRenderTargetID.finalColor = Shader.PropertyToID("_CameraColorRT");
            CameraRenderTargetID.depth = Shader.PropertyToID("_CameraDepthTexture");
            CameraRenderTargetID.depthCopy = Shader.PropertyToID("_CameraCopyDepthTexture");

            m_ShadowMapRT = new RenderTargetIdentifier(m_ShadowMapRTID);

            //m_OpaqueColorRT = new RenderTargetIdentifier(CameraRenderTargetID.opaqueColor);
            m_FinalColorRT = new RenderTargetIdentifier(CameraRenderTargetID.finalColor);
            m_OpaqueDepthRT = new RenderTargetIdentifier(CameraRenderTargetID.depth);
            m_FinalDepthRT = new RenderTargetIdentifier(CameraRenderTargetID.depthCopy);
            m_PostProcessRenderContext = new PostProcessRenderContext();

            m_CopyTextureSupport = SystemInfo.copyTextureSupport;

            // Let engine know we have MSAA on for cases where we support MSAA backbuffer
            if (QualitySettings.antiAliasing != m_Asset.MSAASampleCount)
                QualitySettings.antiAliasing = m_Asset.MSAASampleCount;

            Shader.globalRenderPipeline = "LightweightPipeline";

            m_BlitQuad = LightweightUtils.CreateQuadMesh(false);
            m_BlitMaterial = new Material(m_Asset.BlitShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            m_CopyDepthMaterial = new Material(m_Asset.CopyDepthShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        public override void Dispose()
        {
            base.Dispose();
            Shader.globalRenderPipeline = "";
        }

        CullResults m_CullResults;
        public override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            base.Render(context, cameras);

            bool stereoEnabled = XRSettings.isDeviceActive;

            // TODO: This is at the moment required for all pipes. We should not implicitly change user project settings
            // instead this should be forced when using SRP, since all SRP use linear lighting.
            GraphicsSettings.lightsUseLinearIntensity = true;

            SetupPerFrameShaderConstants(ref context);

            // Sort cameras array by camera depth
            Array.Sort(cameras, m_CameraComparer);
            foreach (Camera camera in cameras)
            {
                m_CurrCamera = camera;
                m_IsOffscreenCamera = m_CurrCamera.targetTexture != null && m_CurrCamera.cameraType != CameraType.SceneView;

                ScriptableCullingParameters cullingParameters;
                if (!CullResults.GetCullingParameters(m_CurrCamera, stereoEnabled, out cullingParameters))
                    continue;

                cullingParameters.shadowDistance = Mathf.Min(m_ShadowSettings.maxShadowDistance,
                    m_CurrCamera.farClipPlane);

#if UNITY_EDITOR
                // Emit scene view UI
                if (camera.cameraType == CameraType.SceneView)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

                CullResults.Cull(ref cullingParameters, context, ref m_CullResults);
                VisibleLight[] visibleLights = m_CullResults.visibleLights.ToArray();

                LightData lightData;
                InitializeLightData(visibleLights, out lightData);

                ShadowPass(visibleLights, ref context, ref lightData);
                ForwardPass(visibleLights, ref context, ref lightData, stereoEnabled);

                // Release temporary RT
                var cmd = CommandBufferPool.Get("After Camera Render");
                cmd.ReleaseTemporaryRT(m_ShadowMapRTID);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.depthCopy);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.depth);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.finalColor);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.opaqueColor);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                context.Submit();
            }
        }

        private void ShadowPass(VisibleLight[] visibleLights, ref ScriptableRenderContext context, ref LightData lightData)
        {
            if (m_Asset.AreShadowsEnabled() && lightData.mainLightIndex != -1)
            {
                VisibleLight mainLight = visibleLights[lightData.mainLightIndex];

                if (mainLight.light.shadows != LightShadows.None)
                {
                    if (!LightweightUtils.IsSupportedShadowType(mainLight.lightType))
                    {
                        Debug.LogWarning("Only directional and spot shadows are supported by LightweightPipeline.");
                        return;
                    }

                    // There's no way to map shadow light indices. We need to pass in the original unsorted index.
                    // If no additional lights then no light sorting is performed and the indices match.
                    int shadowOriginalIndex = (lightData.totalAdditionalLightsCount > 0) ? GetLightUnsortedIndex(lightData.mainLightIndex) : lightData.mainLightIndex;
                    lightData.shadowsRendered = RenderShadows(ref m_CullResults, ref mainLight,
                        shadowOriginalIndex, ref context);
                }
            }
        }

        private void ForwardPass(VisibleLight[] visibleLights, ref ScriptableRenderContext context, ref LightData lightData, bool stereoEnabled)
        {
            FrameRenderingConfiguration frameRenderingConfiguration;
            SetupFrameRendering(out frameRenderingConfiguration);
            SetupIntermediateResources(frameRenderingConfiguration, ref context);
            SetupShaderConstants(visibleLights, ref context, ref lightData);

            // SetupCameraProperties does the following:
            // Setup Camera RenderTarget and Viewport
            // VR Camera Setup and SINGLE_PASS_STEREO props
            // Setup camera view, proj and their inv matrices.
            // Setup properties: _WorldSpaceCameraPos, _ProjectionParams, _ScreenParams, _ZBufferParams, unity_OrthoParams
            // Setup camera world clip planes props
            // setup HDR keyword
            // Setup global time properties (_Time, _SinTime, _CosTime)
            context.SetupCameraProperties(m_CurrCamera, stereoEnabled);

            RendererConfiguration rendererSettings = GetRendererSettings(ref lightData);

            BeginForwardRendering(ref context, frameRenderingConfiguration);
            RenderOpaques(ref context, rendererSettings);
            AfterOpaque(ref context, frameRenderingConfiguration);
            RenderTransparents(ref context, rendererSettings);
            AfterTransparent(ref context, frameRenderingConfiguration);
            EndForwardRendering(ref context, frameRenderingConfiguration);
        }

        private void RenderOpaques(ref ScriptableRenderContext context, RendererConfiguration settings)
        {
            var opaqueDrawSettings = new DrawRendererSettings(m_CurrCamera, m_LitPassName);
            opaqueDrawSettings.SetShaderPassName(1, m_UnlitPassName);
            opaqueDrawSettings.sorting.flags = SortFlags.CommonOpaque;
            opaqueDrawSettings.rendererConfiguration = settings;

            var opaqueFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.opaque
            };

            context.DrawRenderers(m_CullResults.visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);
            context.DrawSkybox(m_CurrCamera);
        }

        private void AfterOpaque(ref ScriptableRenderContext context, FrameRenderingConfiguration config)
        {
            if (!LightweightUtils.HasFlag(config, FrameRenderingConfiguration.RequireDepth))
                return;

            CommandBuffer cmd = CommandBufferPool.Get("After Opaque");
            cmd.SetGlobalTexture(CameraRenderTargetID.depth, m_OpaqueDepthRT);

            // Only takes effect if custom BeforeTransparent PostProcessing effects are active
            // Custom BeforeTransparent custom postfx disabled for VR.
            // TODO: Need to check when post process volume is active/in effect to apply before opaque post processing
            // If active we blit Opaque to FinalColor
            //if (LightweightUtils.HasFlag(config, FrameRenderingConfiguration.PostProcess) && !XRSettings.isDeviceActive)
            //{
            //    RenderPostProcess(cmd, m_OpaqueColorRT, m_FinalColorRT, true);
            //    m_CurrCameraColorRT = (m_IsOffscreenCamera) ? BuiltinRenderTextureType.CameraTarget : m_FinalColorRT;
            //}

            if (m_Asset.SupportsSoftParticles)
            {
                RenderTargetIdentifier colorRT = (m_IsOffscreenCamera) ? BuiltinRenderTextureType.CameraTarget : m_FinalColorRT;
                CopyTexture(cmd, m_OpaqueDepthRT, m_FinalDepthRT, m_CopyDepthMaterial);
                SetupRenderTargets(cmd, colorRT, m_FinalDepthRT);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void AfterTransparent(ref ScriptableRenderContext context, FrameRenderingConfiguration config)
        {
            if (!LightweightUtils.HasFlag(config, FrameRenderingConfiguration.PostProcess))
                return;

            CommandBuffer cmd = CommandBufferPool.Get("After Transparent");
            RenderPostProcess(cmd, BuiltinRenderTextureType.CurrentActive, BuiltinRenderTextureType.CameraTarget, false);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void RenderTransparents(ref ScriptableRenderContext context, RendererConfiguration config)
        {
            var transparentSettings = new DrawRendererSettings(m_CurrCamera, m_LitPassName);
            transparentSettings.SetShaderPassName(1, m_UnlitPassName);
            transparentSettings.sorting.flags = SortFlags.CommonTransparent;
            transparentSettings.rendererConfiguration = config;

            var transparentFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.transparent
            };

            context.DrawRenderers(m_CullResults.visibleRenderers, ref transparentSettings, transparentFilterSettings);
        }

        private void BuildShadowSettings()
        {
            m_ShadowSettings = ShadowSettings.Default;
            m_ShadowSettings.directionalLightCascadeCount = m_Asset.CascadeCount;

            m_ShadowSettings.shadowAtlasWidth = m_Asset.ShadowAtlasResolution;
            m_ShadowSettings.shadowAtlasHeight = m_Asset.ShadowAtlasResolution;
            m_ShadowSettings.maxShadowDistance = m_Asset.ShadowDistance;

            switch (m_ShadowSettings.directionalLightCascadeCount)
            {
                case 1:
                    m_ShadowSettings.directionalLightCascades = new Vector3(1.0f, 0.0f, 0.0f);
                    break;

                case 2:
                    m_ShadowSettings.directionalLightCascades = new Vector3(m_Asset.Cascade2Split, 1.0f, 0.0f);
                    break;

                default:
                    m_ShadowSettings.directionalLightCascades = m_Asset.Cascade4Split;
                    break;
            }
        }

        private void SetupFrameRendering(out FrameRenderingConfiguration configuration)
        {
            configuration = (XRSettings.enabled) ? FrameRenderingConfiguration.Stereo : FrameRenderingConfiguration.None;
            if (XRSettings.enabled && XRSettings.eyeTextureDesc.dimension == TextureDimension.Tex2DArray)
                m_IntermediateTextureArray = true;
            else
                m_IntermediateTextureArray = false;

            bool intermediateTexture = m_CurrCamera.targetTexture != null || m_CurrCamera.cameraType == CameraType.SceneView ||
                                            m_Asset.RenderScale < 1.0f || m_CurrCamera.allowHDR;

            m_ColorFormat = m_CurrCamera.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;

            m_CameraPostProcessLayer = m_CurrCamera.GetComponent<PostProcessLayer>();
            bool postProcessEnabled = m_CameraPostProcessLayer != null && m_CameraPostProcessLayer.enabled;
            if (postProcessEnabled || m_Asset.SupportsSoftParticles)
            {
                configuration |= FrameRenderingConfiguration.RequireDepth;
                intermediateTexture = true;

                if (postProcessEnabled)
                    configuration |= FrameRenderingConfiguration.PostProcess;
            }
            // When post process or soft particles are enabled we disable msaa due to lack of depth resolve
            // One can still use PostFX AA
            else if (m_Asset.MSAASampleCount > 1)
            {
                configuration |= FrameRenderingConfiguration.Msaa;
                intermediateTexture = !LightweightUtils.PlatformSupportsMSAABackBuffer();
            }

            Rect cameraRect = m_CurrCamera.rect;
            if (cameraRect.x > 0.0f || cameraRect.y > 0.0f || cameraRect.width < 1.0f || cameraRect.height < 1.0f)
                intermediateTexture = true;
            else
                configuration |= FrameRenderingConfiguration.DefaultViewport;

            if (intermediateTexture)
                configuration |= FrameRenderingConfiguration.IntermediateTexture;
        }

        private void SetupIntermediateResources(FrameRenderingConfiguration renderingConfig, ref ScriptableRenderContext context)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Setup Intermediate Resources");

            float renderScale = (m_CurrCamera.cameraType == CameraType.Game) ? m_Asset.RenderScale : 1.0f;
            int rtWidth = (int)((float)m_CurrCamera.pixelWidth * renderScale);
            int rtHeight = (int)((float)m_CurrCamera.pixelHeight * renderScale);
            int msaaSamples = (m_IsOffscreenCamera) ? Math.Min(m_CurrCamera.targetTexture.antiAliasing, m_Asset.MSAASampleCount) : m_Asset.MSAASampleCount;
            msaaSamples = (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Msaa)) ? msaaSamples : 1;

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture))
            {
                if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                {
                    RenderTextureDescriptor rtDesc = new RenderTextureDescriptor();
                    rtDesc = XRSettings.eyeTextureDesc;
                    rtDesc.colorFormat = m_ColorFormat;
                    rtDesc.msaaSamples = msaaSamples;

                    cmd.GetTemporaryRT(CameraRenderTargetID.finalColor, rtDesc, FilterMode.Bilinear);
                    //if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    //    cmd.GetTemporaryRT(CameraRenderTargetID.opaqueColor, rtDesc, FilterMode.Bilinear);
                }
                else if (!m_IsOffscreenCamera)
                {
                    cmd.GetTemporaryRT(CameraRenderTargetID.finalColor, rtWidth, rtHeight, kCameraDepthBufferBits,
                        FilterMode.Bilinear, m_ColorFormat, RenderTextureReadWrite.Default, msaaSamples);

                    //if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    //    cmd.GetTemporaryRT(CameraRenderTargetID.opaqueColor, rtWidth, rtHeight, kCameraDepthBufferBits,
                    //        FilterMode.Bilinear, m_ColorFormat, RenderTextureReadWrite.Default, msaaSamples);

                    // When postprocessing is enabled we might have a before transparent effect. In that case we need to
                    // use the camera render target as input. We blit to an opaque RT and then after before postprocessing is done
                    // we blit to the final camera RT. If no postprocessing we blit to final camera RT from beginning.
                    // TODO: We need to have way to check if before opaque post processing is enabled and only then set camera RT to opaqueColorRT
                    //if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    //    m_CurrCameraColorRT = m_OpaqueColorRT;
                    //else
                    //    m_CurrCameraColorRT = m_FinalColorRT;
                }
            }
            //else
            //{
            //    m_CurrCameraColorRT = BuiltinRenderTextureType.CameraTarget;
            //}

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.RequireDepth))
            {
                cmd.GetTemporaryRT(CameraRenderTargetID.depth, rtWidth, rtHeight, kCameraDepthBufferBits, FilterMode.Bilinear, RenderTextureFormat.Depth);

                if (m_Asset.SupportsSoftParticles)
                    cmd.GetTemporaryRT(CameraRenderTargetID.depthCopy, rtWidth, rtHeight, kCameraDepthBufferBits, FilterMode.Bilinear, RenderTextureFormat.Depth);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void SetupShaderConstants(VisibleLight[] visibleLights, ref ScriptableRenderContext context, ref LightData lightData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("SetupShaderConstants");
            SetupShaderLightConstants(cmd, visibleLights, ref lightData);
            SetShaderKeywords(cmd, ref lightData, visibleLights);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void InitializeLightData(VisibleLight[] visibleLights, out LightData lightData)
        {
            int visibleLightsCount = visibleLights.Length;
            m_SortedLightIndexMap.Clear();

            lightData.shadowsRendered = false;

            // We always support at least one per-pixel light, which is main light. Shade objects up to a limit of per-object
            // pixel lights defined in the pipeline settings.
            int maxSupportedPixelLights = Math.Min(m_Asset.MaxAdditionalPixelLights, kMaxPerObjectAdditionalLights) + 1;
            int maxPixelLights = Math.Min(maxSupportedPixelLights, visibleLightsCount);

            // If vertex lighting is enabled in the pipeline settings, then we shade the remaining visible lights per-vertex
            // up to the maximum amount of per-object lights.
            int vertexLights = (m_Asset.SupportsVertexLight) ? kMaxPerObjectAdditionalLights + 1 - maxPixelLights : 0;

            if (visibleLightsCount <= 1)
                lightData.mainLightIndex = GetMainLight(visibleLights);
            else
                lightData.mainLightIndex = SortLights(visibleLights);

            lightData.pixelAdditionalLightsCount = Math.Max(0, maxPixelLights - 1);
            lightData.totalAdditionalLightsCount = lightData.pixelAdditionalLightsCount + vertexLights;
        }

        private int SortLights(VisibleLight[] visibleLights)
        {
            int totalVisibleLights = visibleLights.Length;

            Dictionary<int, int> visibleLightsIDMap = new Dictionary<int, int>();
            for (int i = 0; i < totalVisibleLights; ++i)
                visibleLightsIDMap.Add(visibleLights[i].GetHashCode(), i);

            // Sorts light so we have all directionals first, then local lights.
            // Directionals are sorted further by shadow, cookie and intensity
            // Locals are sorted further by shadow, cookie and distance to camera
            m_LightCompararer.CurrCamera = m_CurrCamera;
            Array.Sort(visibleLights, m_LightCompararer);

            for (int i = 0; i < totalVisibleLights; ++i)
                m_SortedLightIndexMap.Add(visibleLightsIDMap[visibleLights[i].GetHashCode()]);

            return GetMainLight(visibleLights);
        }

        // How main light is decided:
        // If shadows enabled, main light is always a shadow casting light. Directional has priority over local lights.
        // Otherwise directional lights have priority based on cookie support and intensity
        private int GetMainLight(VisibleLight[] visibleLights)
        {
            int totalVisibleLights = visibleLights.Length;
            bool shadowsEnabled = m_Asset.AreShadowsEnabled();

            if (totalVisibleLights == 0)
                return -1;

            int brighestDirectionalIndex = -1;
            for (int i = 0; i < totalVisibleLights; ++i)
            {
                VisibleLight currLight = visibleLights[i];

                // Particle system lights have the light property as null. We sort lights so all particles lights
                // come last. Therefore, if first light is particle light then all lights are particle lights.
                // In this case we either have no main light or already found it.
                if (currLight.light == null)
                    break;

                if (shadowsEnabled && currLight.light.shadows != LightShadows.None && LightweightUtils.IsSupportedShadowType(currLight.lightType))
                    return i;

                if (currLight.lightType == LightType.Directional && brighestDirectionalIndex == -1)
                    brighestDirectionalIndex = i;
            }

            return brighestDirectionalIndex;
        }

        private void InitializeLightConstants(VisibleLight[] lights, int lightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightDistanceAttenuation, out Vector4 lightSpotDir,
            out Vector4 lightSpotAttenuation)
        {
            lightPos = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
            lightColor = Color.black;
            lightDistanceAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);
            lightSpotDir = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
            lightSpotAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);

            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            VisibleLight light = lights[lightIndex];
            if (light.lightType == LightType.Directional)
            {
                Vector4 dir = -light.localToWorld.GetColumn(2);
                lightPos = new Vector4(dir.x, dir.y, dir.z, 0.0f);
            }
            else
            {
                Vector4 pos = light.localToWorld.GetColumn(3);
                lightPos = new Vector4(pos.x, pos.y, pos.z, 1.0f);
            }

            lightColor = light.finalColor;

            // Directional Light attenuation is initialize so distance attenuation always be 1.0
            if (light.lightType != LightType.Directional)
            {
                // Light attenuation in lightweight matches the unity vanilla one.
                // attenuation = 1.0 / 1.0 + distanceToLightSqr * quadraticAttenuation
                // then a smooth factor is applied to linearly fade attenuation to light range
                // the attenuation smooth factor starts having effect at 80% of light range
                // smoothFactor = (lightRangeSqr - distanceToLightSqr) / (lightRangeSqr - fadeStartDistanceSqr)
                // We rewrite smoothFactor to be able to pre compute the constant terms below and apply the smooth factor
                // with one MAD instruction
                // smoothFactor =  distanceSqr * (1.0 / (fadeDistanceSqr - lightRangeSqr)) + (-lightRangeSqr / (fadeDistanceSqr - lightRangeSqr)
                //                 distanceSqr *           oneOverFadeRangeSqr             +              lightRangeSqrOverFadeRangeSqr
                float lightRangeSqr = light.range * light.range;
                float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
                float fadeRangeSqr = (fadeStartDistanceSqr - lightRangeSqr);
                float oneOverFadeRangeSqr = 1.0f / fadeRangeSqr;
                float lightRangeSqrOverFadeRangeSqr = -lightRangeSqr / fadeRangeSqr;
                float quadAtten = 25.0f / lightRangeSqr;
                lightDistanceAttenuation = new Vector4(quadAtten, oneOverFadeRangeSqr, lightRangeSqrOverFadeRangeSqr, 0.0f);
            }

            if (light.lightType == LightType.Spot)
            {
                Vector4 dir = light.localToWorld.GetColumn(2);
                lightSpotDir = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);

                // Spot Attenuation with a linear falloff can be defined as
                // (SdotL - cosOuterAngle) / (cosInnerAngle - cosOuterAngle)
                // This can be rewritten as
                // invAngleRange = 1.0 / (cosInnerAngle - cosOuterAngle)
                // SdotL * invAngleRange + (-cosOuterAngle * invAngleRange)
                // If we precompute the terms in a MAD instruction
                float spotAngle = Mathf.Deg2Rad * light.spotAngle;
                float cosOuterAngle = Mathf.Cos(spotAngle * 0.5f);
                float cosInneAngle = Mathf.Cos(spotAngle * 0.25f);
                float smoothAngleRange = cosInneAngle - cosOuterAngle;
                if (Mathf.Approximately(smoothAngleRange, 0.0f))
                    smoothAngleRange = 1.0f;

                float invAngleRange = 1.0f / smoothAngleRange;
                float add = -cosOuterAngle * invAngleRange;
                lightSpotAttenuation = new Vector4(invAngleRange, add, 0.0f);
            }
        }

        private void SetupPerFrameShaderConstants(ref ScriptableRenderContext context)
        {
            // When glossy reflections are OFF in the shader we set a constant color to use as indirect specular
            SphericalHarmonicsL2 ambientSH = RenderSettings.ambientProbe;
            Vector4 glossyEnvColor = new Vector4(ambientSH[0, 0], ambientSH[1, 0], ambientSH[2, 0]) * RenderSettings.reflectionIntensity;
            Shader.SetGlobalVector(PerFrameBuffer._GlossyEnvironmentColor, glossyEnvColor);
        }

        private void SetupShaderLightConstants(CommandBuffer cmd, VisibleLight[] lights, ref LightData lightData)
        {
            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Lightweight pipeline also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, lights, lightData.mainLightIndex);
            if (lightData.shadowsRendered)
                SetupShadowShaderConstants(cmd, ref lights[lightData.mainLightIndex], m_ShadowCasterCascadesCount);

            if (lightData.totalAdditionalLightsCount > 0)
                SetupAdditionalListConstants(cmd, lights, ref lightData);
        }

        private void SetupMainLightConstants(CommandBuffer cmd, VisibleLight[] lights, int lightIndex)
        {
            Vector4 lightPos, lightColor, lightDistanceAttenuation, lightSpotDir, lightSpotAttenuation;
            InitializeLightConstants(lights, lightIndex, out lightPos, out lightColor, out lightDistanceAttenuation, out lightSpotDir, out lightSpotAttenuation);

            cmd.SetGlobalVector(PerCameraBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalColor(PerCameraBuffer._MainLightColor, lightColor);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightDistanceAttenuation, lightDistanceAttenuation);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightSpotDir, lightSpotDir);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightSpotAttenuation, lightSpotAttenuation);

            if (lightIndex >= 0 && LightweightUtils.IsSupportedCookieType(lights[lightIndex].lightType) && lights[lightIndex].light.cookie != null)
            {
                Matrix4x4 lightCookieMatrix;
                LightweightUtils.GetLightCookieMatrix(lights[lightIndex], out lightCookieMatrix);
                cmd.SetGlobalTexture(PerCameraBuffer._MainLightCookie, lights[lightIndex].light.cookie);
                cmd.SetGlobalMatrix(PerCameraBuffer._WorldToLight, lightCookieMatrix);
            }
        }

        private void SetupAdditionalListConstants(CommandBuffer cmd, VisibleLight[] lights, ref LightData lightData)
        {
            int additionalLightIndex = 0;

            // We need to update per-object light list with the proper map to our global additional light buffer
            // First we initialize all lights in the map to -1 to tell the system to discard main light index and
            // remaining lights in the scene that don't fit the max additional light buffer (kMaxVisibileAdditionalLights)
            int[] perObjectLightIndexMap = m_CullResults.GetLightIndexMap();
            for (int i = 0; i < lights.Length; ++i)
                perObjectLightIndexMap[i] = -1;

            for (int i = 0; i < lights.Length && additionalLightIndex < kMaxVisibleAdditionalLights; ++i)
            {
                if (i != lightData.mainLightIndex)
                {
                    // The engine performs per-object light culling and initialize 8 light indices into two vec4 constants unity_4LightIndices0 and unity_4LightIndices1.
                    // In the shader we iterate over each visible light using the indices provided in these constants to index our global light buffer
                    // ex: first light position would be m_LightPosisitions[unity_4LightIndices[0]];

                    // However since we sorted the lights we need to tell the engine how to map the original/unsorted indices to our global buffer
                    // We do it by settings the perObjectLightIndexMap to the appropriate additionalLightIndex.
                    perObjectLightIndexMap[GetLightUnsortedIndex(i)] = additionalLightIndex;
                    InitializeLightConstants(lights, i, out m_LightPositions[additionalLightIndex],
                            out m_LightColors[additionalLightIndex],
                            out m_LightDistanceAttenuations[additionalLightIndex],
                            out m_LightSpotDirections[additionalLightIndex],
                            out m_LightSpotAttenuations[additionalLightIndex]);
                    additionalLightIndex++;
                }
            }
            m_CullResults.SetLightIndexMap(perObjectLightIndexMap);

            cmd.SetGlobalVector(PerCameraBuffer._AdditionalLightCount, new Vector4 (lightData.pixelAdditionalLightsCount,
                 lightData.totalAdditionalLightsCount, 0.0f, 0.0f));
            cmd.SetGlobalVectorArray (PerCameraBuffer._AdditionalLightPosition, m_LightPositions);
            cmd.SetGlobalVectorArray (PerCameraBuffer._AdditionalLightColor, m_LightColors);
            cmd.SetGlobalVectorArray (PerCameraBuffer._AdditionalLightDistanceAttenuation, m_LightDistanceAttenuations);
            cmd.SetGlobalVectorArray (PerCameraBuffer._AdditionalLightSpotDir, m_LightSpotDirections);
            cmd.SetGlobalVectorArray (PerCameraBuffer._AdditionalLightSpotAttenuation, m_LightSpotAttenuations);
        }

        private void SetupShadowShaderConstants(CommandBuffer cmd, ref VisibleLight shadowLight, int cascadeCount)
        {
            Light light = shadowLight.light;
            float bias = light.shadowBias * 0.1f;
            float normalBias = light.shadowNormalBias;
            float shadowResolution = m_ShadowSlices[0].shadowResolution;

            const int maxShadowCascades = 4;
            Matrix4x4[] shadowMatrices = new Matrix4x4[maxShadowCascades];
            for (int i = 0; i < cascadeCount; ++i)
                shadowMatrices[i] = (cascadeCount >= i) ? m_ShadowSlices[i].shadowTransform : Matrix4x4.identity;

            // TODO: shadow resolution per cascade in case cascades endup being supported.
            float invShadowResolution = 1.0f / shadowResolution;
            float[] pcfKernel =
            {
                -0.5f * invShadowResolution, 0.5f * invShadowResolution,
                0.5f * invShadowResolution, 0.5f * invShadowResolution,
                -0.5f * invShadowResolution, -0.5f * invShadowResolution,
                0.5f * invShadowResolution, -0.5f * invShadowResolution
            };

            cmd.SetGlobalMatrixArray("_WorldToShadow", shadowMatrices);
            cmd.SetGlobalVectorArray("_DirShadowSplitSpheres", m_DirectionalShadowSplitDistances);
            cmd.SetGlobalVector("_ShadowData", new Vector4(0.0f, bias, normalBias, 0.0f));
            cmd.SetGlobalFloatArray("_PCFKernel", pcfKernel);
        }

        private void SetShaderKeywords(CommandBuffer cmd, ref LightData lightData, VisibleLight[] visibleLights)
        {
            int vertexLightsCount = lightData.totalAdditionalLightsCount - lightData.pixelAdditionalLightsCount;
            LightweightUtils.SetKeyword(cmd, "_VERTEX_LIGHTS", vertexLightsCount > 0);

            int mainLightIndex = lightData.mainLightIndex;

            // Currently only directional light cookie is supported
            LightweightUtils.SetKeyword(cmd, "_MAIN_LIGHT_COOKIE", mainLightIndex != -1 && LightweightUtils.IsSupportedCookieType(visibleLights[mainLightIndex].lightType) && visibleLights[mainLightIndex].light.cookie != null);
            LightweightUtils.SetKeyword (cmd, "_MAIN_DIRECTIONAL_LIGHT", mainLightIndex == -1 || visibleLights[mainLightIndex].lightType == LightType.Directional);
            LightweightUtils.SetKeyword (cmd, "_MAIN_SPOT_LIGHT", mainLightIndex != -1 && visibleLights[mainLightIndex].lightType == LightType.Spot);
            LightweightUtils.SetKeyword (cmd, "_MAIN_POINT_LIGHT", mainLightIndex != -1 && visibleLights[mainLightIndex].lightType == LightType.Point);
            LightweightUtils.SetKeyword(cmd, "_ADDITIONAL_LIGHTS", lightData.totalAdditionalLightsCount > 0);

            string[] shadowKeywords = new string[] { "_HARD_SHADOWS", "_SOFT_SHADOWS", "_HARD_SHADOWS_CASCADES", "_SOFT_SHADOWS_CASCADES" };
            for (int i = 0; i < shadowKeywords.Length; ++i)
                cmd.DisableShaderKeyword(shadowKeywords[i]);

            if (m_Asset.AreShadowsEnabled() && lightData.shadowsRendered)
            {
                int keywordIndex = (int)m_Asset.ShadowSetting - 1;
                if (m_Asset.CascadeCount > 1)
                    keywordIndex += 2;
                cmd.EnableShaderKeyword(shadowKeywords[keywordIndex]);
            }

            LightweightUtils.SetKeyword(cmd, "SOFTPARTICLES_ON", m_Asset.SupportsSoftParticles);
        }

        private bool RenderShadows(ref CullResults cullResults, ref VisibleLight shadowLight, int shadowLightIndex, ref ScriptableRenderContext context)
        {
            m_ShadowCasterCascadesCount = m_ShadowSettings.directionalLightCascadeCount;

            if (shadowLight.lightType == LightType.Spot)
                m_ShadowCasterCascadesCount = 1;

            int shadowResolution = GetMaxTileResolutionInAtlas(m_ShadowSettings.shadowAtlasWidth, m_ShadowSettings.shadowAtlasHeight, m_ShadowCasterCascadesCount);

            Bounds bounds;
            if (!cullResults.GetShadowCasterBounds(shadowLightIndex, out bounds))
                return false;

            var setRenderTargetCommandBuffer = CommandBufferPool.Get();
            setRenderTargetCommandBuffer.name = "Render packed shadows";
            setRenderTargetCommandBuffer.GetTemporaryRT(m_ShadowMapRTID, m_ShadowSettings.shadowAtlasWidth,
                m_ShadowSettings.shadowAtlasHeight, kShadowDepthBufferBits, FilterMode.Bilinear, RenderTextureFormat.Depth);
            setRenderTargetCommandBuffer.SetRenderTarget(m_ShadowMapRT);
            setRenderTargetCommandBuffer.ClearRenderTarget(true, true, Color.black);
            context.ExecuteCommandBuffer(setRenderTargetCommandBuffer);
            CommandBufferPool.Release(setRenderTargetCommandBuffer);

            float shadowNearPlane = m_Asset.ShadowNearOffset;
            Vector3 splitRatio = m_ShadowSettings.directionalLightCascades;

            Matrix4x4 view, proj;
            var settings = new DrawShadowsSettings(cullResults, shadowLightIndex);
            bool needRendering = false;

            if (shadowLight.lightType == LightType.Spot)
            {
                needRendering = cullResults.ComputeSpotShadowMatricesAndCullingPrimitives(shadowLightIndex, out view, out proj,
                        out settings.splitData);

                if (!needRendering)
                    return false;

                SetupShadowSliceTransform(0, shadowResolution, proj, view);
                RenderShadowSlice(ref context, 0, proj, view, settings);
            }
            else if (shadowLight.lightType == LightType.Directional)
            {
                for (int cascadeIdx = 0; cascadeIdx < m_ShadowCasterCascadesCount; ++cascadeIdx)
                {
                    needRendering = cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(shadowLightIndex,
                            cascadeIdx, m_ShadowCasterCascadesCount, splitRatio, shadowResolution, shadowNearPlane, out view, out proj,
                            out settings.splitData);

                    m_DirectionalShadowSplitDistances[cascadeIdx] = settings.splitData.cullingSphere;
                    m_DirectionalShadowSplitDistances[cascadeIdx].w *= settings.splitData.cullingSphere.w;

                    if (!needRendering)
                        return false;

                    SetupShadowSliceTransform(cascadeIdx, shadowResolution, proj, view);
                    RenderShadowSlice(ref context, cascadeIdx, proj, view, settings);
                }
            }
            else
            {
                Debug.LogWarning("Only spot and directional shadow casters are supported in lightweight pipeline");
                return false;
            }

            return true;
        }

        private void SetupShadowSliceTransform(int cascadeIndex, int shadowResolution, Matrix4x4 proj, Matrix4x4 view)
        {
            // Assumes MAX_CASCADES = 4
            m_ShadowSlices[cascadeIndex].atlasX = (cascadeIndex % 2) * shadowResolution;
            m_ShadowSlices[cascadeIndex].atlasY = (cascadeIndex / 2) * shadowResolution;
            m_ShadowSlices[cascadeIndex].shadowResolution = shadowResolution;
            m_ShadowSlices[cascadeIndex].shadowTransform = Matrix4x4.identity;

            var matScaleBias = Matrix4x4.identity;
            matScaleBias.m00 = 0.5f;
            matScaleBias.m11 = 0.5f;
            matScaleBias.m22 = 0.5f;
            matScaleBias.m03 = 0.5f;
            matScaleBias.m23 = 0.5f;
            matScaleBias.m13 = 0.5f;

            // Later down the pipeline the proj matrix will be scaled to reverse-z in case of DX.
            // We need account for that scale in the shadowTransform.
            if (SystemInfo.usesReversedZBuffer)
                matScaleBias.m22 = -0.5f;

            var matTile = Matrix4x4.identity;
            matTile.m00 = (float)m_ShadowSlices[cascadeIndex].shadowResolution /
                (float)m_ShadowSettings.shadowAtlasWidth;
            matTile.m11 = (float)m_ShadowSlices[cascadeIndex].shadowResolution /
                (float)m_ShadowSettings.shadowAtlasHeight;
            matTile.m03 = (float)m_ShadowSlices[cascadeIndex].atlasX / (float)m_ShadowSettings.shadowAtlasWidth;
            matTile.m13 = (float)m_ShadowSlices[cascadeIndex].atlasY / (float)m_ShadowSettings.shadowAtlasHeight;

            m_ShadowSlices[cascadeIndex].shadowTransform = matTile * matScaleBias * proj * view;
        }

        private void RenderShadowSlice(ref ScriptableRenderContext context, int cascadeIndex,
            Matrix4x4 proj, Matrix4x4 view, DrawShadowsSettings settings)
        {
            var buffer = CommandBufferPool.Get("Prepare Shadowmap Slice");
            buffer.SetViewport(new Rect(m_ShadowSlices[cascadeIndex].atlasX, m_ShadowSlices[cascadeIndex].atlasY,
                    m_ShadowSlices[cascadeIndex].shadowResolution, m_ShadowSlices[cascadeIndex].shadowResolution));
            buffer.SetViewProjectionMatrices(view, proj);
            context.ExecuteCommandBuffer(buffer);

            context.DrawShadows(ref settings);
            CommandBufferPool.Release(buffer);
        }

        private int GetMaxTileResolutionInAtlas(int atlasWidth, int atlasHeight, int tileCount)
        {
            int resolution = Mathf.Min(atlasWidth, atlasHeight);
            if (tileCount > Mathf.Log(resolution))
            {
                Debug.LogError(
                    String.Format(
                        "Cannot fit {0} tiles into current shadowmap atlas of size ({1}, {2}). ShadowMap Resolution set to zero.",
                        tileCount, atlasWidth, atlasHeight));
                return 0;
            }

            int currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
            while (currentTileCount < tileCount)
            {
                resolution = resolution >> 1;
                currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
            }
            return resolution;
        }

        private void BeginForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig)
        {
            RenderTargetIdentifier colorRT = BuiltinRenderTextureType.CameraTarget;
            RenderTargetIdentifier depthRT = BuiltinRenderTextureType.None;

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                context.StartMultiEye(m_CurrCamera);

            CommandBuffer cmd = CommandBufferPool.Get("SetCameraRenderTarget");
            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture))
            {
                if (!m_IsOffscreenCamera)
                    colorRT = m_FinalColorRT;

                if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.RequireDepth))
                    depthRT = m_OpaqueDepthRT;
            }

            SetupRenderTargets(cmd, colorRT, depthRT);

            // Clear RenderTarget to avoid tile initialization on mobile GPUs
            // https://community.arm.com/graphics/b/blog/posts/mali-performance-2-how-to-correctly-handle-framebuffers
            if (m_CurrCamera.clearFlags != CameraClearFlags.Nothing)
            {
                bool clearDepth = (m_CurrCamera.clearFlags != CameraClearFlags.Nothing);
                bool clearColor = (m_CurrCamera.clearFlags == CameraClearFlags.Color || m_CurrCamera.clearFlags == CameraClearFlags.Skybox);
                cmd.ClearRenderTarget(clearDepth, clearColor, m_CurrCamera.backgroundColor.linear);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void EndForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig)
        {
            // No additional rendering needs to be done if this is an off screen rendering camera
            if (m_IsOffscreenCamera)
                return;

            var cmd = CommandBufferPool.Get("Blit");
            if (m_IntermediateTextureArray)
            {
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, 0, CubemapFace.Unknown, -1);
                cmd.Blit(m_FinalColorRT, BuiltinRenderTextureType.CurrentActive);
            }
            else if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture))
            {
                // If PostProcessing is enabled, it is already blit to CameraTarget.
                if (!LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    Blit(cmd, renderingConfig, BuiltinRenderTextureType.CurrentActive, BuiltinRenderTextureType.CameraTarget);
            }

            SetupRenderTargets(cmd, BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.None);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
            {
                context.StopMultiEye(m_CurrCamera);
                context.StereoEndRender(m_CurrCamera);
            }
        }

        RendererConfiguration GetRendererSettings(ref LightData lightData)
        {
            RendererConfiguration settings = RendererConfiguration.PerObjectReflectionProbes | RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe;
            if (lightData.totalAdditionalLightsCount > 0)
                settings |= RendererConfiguration.PerObjectLightIndices8;
            return settings;
        }

        private void SetupRenderTargets(CommandBuffer cmd, RenderTargetIdentifier colorRT, RenderTargetIdentifier depthRT)
        {
            int depthSlice = (m_IntermediateTextureArray) ? -1 : 0;
            if (depthRT != BuiltinRenderTextureType.None)
                cmd.SetRenderTarget(colorRT, depthRT, 0, CubemapFace.Unknown, depthSlice);
            else
                cmd.SetRenderTarget(colorRT, 0, CubemapFace.Unknown, depthSlice);
        }

        private void RenderPostProcess(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier dest, bool opaqueOnly)
        {
            m_PostProcessRenderContext.Reset();
            m_PostProcessRenderContext.camera = m_CurrCamera;
            m_PostProcessRenderContext.source = source;
            m_PostProcessRenderContext.sourceFormat = m_ColorFormat;
            m_PostProcessRenderContext.destination = dest;
            m_PostProcessRenderContext.command = cmd;
            m_PostProcessRenderContext.flip = true;

            if (opaqueOnly)
                m_CameraPostProcessLayer.RenderOpaqueOnly(m_PostProcessRenderContext);
            else
                m_CameraPostProcessLayer.Render(m_PostProcessRenderContext);
        }

        private int GetLightUnsortedIndex(int index)
        {
            return (index < m_SortedLightIndexMap.Count) ? m_SortedLightIndexMap[index] : index;
        }

        private void Blit(CommandBuffer cmd, FrameRenderingConfiguration renderingConfig, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, Material material = null)
        {
            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.DefaultViewport))
            {
                cmd.Blit(sourceRT, destRT, material);
            }
            else
            {
                if (m_BlitQuad == null)
                    m_BlitQuad = LightweightUtils.CreateQuadMesh(false);

                cmd.SetGlobalTexture(m_BlitTexID, sourceRT);
                cmd.SetRenderTarget(destRT);
                cmd.SetViewport(m_CurrCamera.pixelRect);
                cmd.DrawMesh(m_BlitQuad, Matrix4x4.identity, m_BlitMaterial);
            }
        }

        private void CopyTexture(CommandBuffer cmd, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, Material copyMaterial)
        {
            if (m_CopyTextureSupport != CopyTextureSupport.None)
                cmd.CopyTexture(sourceRT, destRT);
            else
                cmd.Blit(sourceRT, destRT, copyMaterial);
        }
    }
}
