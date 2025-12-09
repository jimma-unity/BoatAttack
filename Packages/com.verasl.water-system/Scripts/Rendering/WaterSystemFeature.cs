using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using System;

namespace WaterSystem
{
    public class WaterSystemFeature : ScriptableRendererFeature
    {
        #region Water Effects Pass

        class WaterFxPass : ScriptableRenderPass
        {
            private const string k_RenderWaterFXTag = "Render Water FX";
            private const string k_WaterFXMapName = "_WaterFXMap";
            private readonly int m_WaterFXMapID = Shader.PropertyToID(k_WaterFXMapName);
            private ProfilingSampler m_WaterFX_Profile = new(k_RenderWaterFXTag);
            private readonly ShaderTagId m_WaterFXShaderTag = new("WaterFX");
            private readonly Color m_ClearColor = new(0.0f, 0.5f, 0.5f, 0.5f); //r = foam mask, g = normal.x, b = normal.z, a = displacement
            private FilteringSettings m_FilteringSettings;

            public class WaterFxData : ContextItem, IDisposable
            {
                private RTHandle m_RTHandle;
                public TextureHandle m_TextureHandle;

                public void Init(RenderGraph renderGraph, UniversalCameraData cameraData)
                {
                    // Setup the descriptor we use. We should use the camera target's descriptor as a start.
                    var targetDescriptor = cameraData.cameraTargetDescriptor;
                    ConfigureCameraDescriptor(ref targetDescriptor);

                    // Reallocate if the RTHandles are being initialized for the first time or if the targetDescriptor has changed since last frame.
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_RTHandle, targetDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: k_WaterFXMapName);
                    
                    if (!m_TextureHandle.IsValid())
                    {
                        // Create the texture handles inside render graph by importing the RTHandles in render graph.
                        m_TextureHandle = renderGraph.ImportTexture(m_RTHandle);
                    }
                }

                // We will need to reset the texture handle after each frame to avoid leaking invalid texture handles
                // since a texture handle only lives for one frame.
                public override void Reset()
                {
                    // Resets the color buffer to avoid carrying invalid references to the next frame.
                    // This could be a texture handle from last frame which will now be invalid.
                    m_TextureHandle = TextureHandle.nullHandle;
                }

                // We need to release the texture once the renderer is released which will dispose every item inside
                // frameData (also data types previously created in earlier frames).
                public void Dispose()
                {
                    m_RTHandle?.Release();
                }
            }

            private class WaterFxPassData
            {
                public RendererListHandle renderListHdl;
                public Color clearColor;
            }

            public WaterFxPass()
            {
                // only render transparent objects
                m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            }

            public static void ConfigureCameraDescriptor(ref RenderTextureDescriptor cameraTextureDescriptor)
            {
                // no need for a depth buffer
                cameraTextureDescriptor.depthBufferBits = 0;
                // Half resolution
                cameraTextureDescriptor.width /= 2;
                cameraTextureDescriptor.height /= 2;
                // default format TODO research usefulness of HDR format
                cameraTextureDescriptor.colorFormat = RenderTextureFormat.Default;
            }

            // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
            static void ExecutePass(WaterFxPassData data, RasterGraphContext context)
            {
                context.cmd.ClearRenderTarget(false, true, data.clearColor);
                context.cmd.DrawRendererList(data.renderListHdl);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer contextContainer)
            {
                // Create the WaterFxData inside frameData.
                var waterFxData = contextContainer.GetOrCreate<WaterFxData>();
                var cameraData = contextContainer.Get<UniversalCameraData>();

                waterFxData.Init(renderGraph, cameraData);

                using (var builder = renderGraph.AddRasterRenderPass<WaterFxPassData>(k_RenderWaterFXTag, out var passData, m_WaterFX_Profile))
                {
                    var renderingData = contextContainer.Get<UniversalRenderingData>();
                    var lightData = contextContainer.Get<UniversalLightData>();

                    builder.SetRenderAttachment(waterFxData.m_TextureHandle, 0);

                     // Create a RenderList to draw all the renderers matching the "WaterFX" shader pass
                    var drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
                    var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, m_FilteringSettings);
                    passData.renderListHdl = renderGraph.CreateRendererList(rendererListParams);
                    builder.UseRendererList(passData.renderListHdl);
                    passData.clearColor = m_ClearColor;

                    builder.SetRenderFunc((WaterFxPassData passData, RasterGraphContext rgContext) => ExecutePass(passData, rgContext));
                    builder.SetGlobalTextureAfterPass(waterFxData.m_TextureHandle, m_WaterFXMapID);
                }
            }
        }

        #endregion

        #region Caustics Pass

        class WaterCausticsPass : ScriptableRenderPass
        {
            private const string k_RenderWaterCausticsTag = "Render Water Caustics";
            private ProfilingSampler m_WaterCaustics_Profile = new (k_RenderWaterCausticsTag);
            public Material WaterCausticMaterial;
            private Mesh m_mesh;

            private class CausticsPassData
            {
                public Vector3 cameraPosition;
            }

            bool ExecutionCheck(UniversalCameraData camData, UniversalResourceData resourceData)
            {
                if (resourceData.activeColorTexture.IsValid() == false) return false;
                return WaterCausticMaterial != null;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer contextContainer)
            {
                UniversalCameraData cameraData = contextContainer.Get<UniversalCameraData>();
                UniversalResourceData resourceData = contextContainer.Get<UniversalResourceData>();

                if (!ExecutionCheck(cameraData, resourceData)) return;

                using (var builder = renderGraph.AddRasterRenderPass<CausticsPassData>(k_RenderWaterCausticsTag, out var passData, m_WaterCaustics_Profile))
                {
                    passData.cameraPosition = cameraData.worldSpaceCameraPos;

                    // set buffers
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                    builder.UseTexture(resourceData.cameraDepthTexture);

                    builder.SetRenderFunc((CausticsPassData data, RasterGraphContext context) =>
                    {
                        var sunMatrix = RenderSettings.sun != null
                        ? RenderSettings.sun.transform.localToWorldMatrix
                        : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
                        WaterCausticMaterial.SetMatrix("_MainLightDir", sunMatrix);

                        // Create mesh if needed
                        if (!m_mesh)
                            m_mesh = GenerateCausticsMesh(1000f);

                        // Create the matrix to position the caustics mesh.
                        var position = data.cameraPosition;
                        position.y = 0; // TODO should read a global 'water height' variable.
                        var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);

                        context.cmd.DrawMesh(m_mesh, matrix, WaterCausticMaterial, 0, 0);
                    });
                }
            }
        }

        #endregion

        WaterFxPass m_WaterFxPass;
        WaterCausticsPass m_CausticsPass;

        public WaterSystemSettings settings = new();
        [HideInInspector][SerializeField] private Shader causticShader;
        [HideInInspector][SerializeField] private Texture2D causticTexture;

        private Material _causticMaterial;

        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int Size = Shader.PropertyToID("_Size");
        private static readonly int CausticTexture = Shader.PropertyToID("_CausticMap");

        public override void Create()
        {
            // WaterFX Pass
            m_WaterFxPass = new WaterFxPass {renderPassEvent = RenderPassEvent.BeforeRenderingOpaques};

            // Caustic Pass
            m_CausticsPass = new WaterCausticsPass();

            causticShader = causticShader ? causticShader : Shader.Find("Hidden/BoatAttack/Caustics");
            if (causticShader == null) return;
            if (_causticMaterial)
            {
                DestroyImmediate(_causticMaterial);
            }
            _causticMaterial = CoreUtils.CreateEngineMaterial(causticShader);
            _causticMaterial.SetFloat("_BlendDistance", settings.causticBlendDistance);
            
            if (causticTexture == null)
            {
                Debug.Log("Caustics Texture missing, attempting to load.");
#if UNITY_EDITOR
                causticTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.verasl.water-system/Textures/WaterSurface_single.tif");
#endif
            }
            _causticMaterial.SetTexture(CausticTexture, causticTexture);
            
            // TODO Fix debug settings.
            /*switch (settings.debug)
            {
                case WaterSystemSettings.DebugMode.Caustics:
                    _causticMaterial.SetFloat(SrcBlend, 1f);
                    _causticMaterial.SetFloat(DstBlend, 0f);
                    _causticMaterial.EnableKeyword("_DEBUG");
                    m_CausticsPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                    break;
                case WaterSystemSettings.DebugMode.WaterEffects:
                    break;
                case WaterSystemSettings.DebugMode.Disabled:
                    // Caustics
                    _causticMaterial.SetFloat(SrcBlend, 2f);
                    _causticMaterial.SetFloat(DstBlend, 0f);
                    _causticMaterial.DisableKeyword("_DEBUG");
                    m_CausticsPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1;
                    // WaterEffects
                    break;
            }*/

            _causticMaterial.SetFloat(Size, settings.causticScale);
            m_CausticsPass.WaterCausticMaterial = _causticMaterial;
        }

        bool ShouldEnqueueForCamera(Camera camera)
        {
            return camera.cameraType == CameraType.SceneView || camera.CompareTag("MainCamera");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if(ShouldEnqueueForCamera(renderingData.cameraData.camera))
            {
                m_CausticsPass.ConfigureInput(ScriptableRenderPassInput.Depth);

                renderer.EnqueuePass(m_WaterFxPass);
                renderer.EnqueuePass(m_CausticsPass);
            }
        }

        /// <summary>
        /// This function Generates a flat quad for use with the caustics pass.
        /// </summary>
        /// <param name="size">The length of the quad.</param>
        /// <returns></returns>
        private static Mesh GenerateCausticsMesh(float size)
        {
            var m = new Mesh();
            size *= 0.5f;

            var verts = new[]
            {
                new Vector3(-size, 0f, -size),
                new Vector3(size, 0f, -size),
                new Vector3(-size, 0f, size),
                new Vector3(size, 0f, size)
            };
            m.vertices = verts;

            var tris = new[]
            {
                0, 2, 1,
                2, 3, 1
            };
            m.triangles = tris;

            return m;
        }

        [Serializable]
        public class WaterSystemSettings
        {
            [Header("Caustics Settings")] [Range(0.1f, 1f)]
            public float causticScale = 0.25f;

            public float causticBlendDistance = 3f;

            [Header("Advanced Settings")] public DebugMode debug = DebugMode.Disabled;

            public enum DebugMode
            {
                Disabled,
                WaterEffects,
                Caustics
            }
        }
    }
}