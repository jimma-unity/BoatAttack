﻿using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
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
            private ProfilingSampler m_WaterFX_Profile = new ProfilingSampler(k_RenderWaterFXTag);
            private readonly ShaderTagId m_WaterFXShaderTag = new ShaderTagId("WaterFX");
            private readonly Color m_ClearColor = new Color(0.0f, 0.5f, 0.5f, 0.5f); //r = foam mask, g = normal.x, b = normal.z, a = displacement
            private FilteringSettings m_FilteringSettings;
            private RTHandle m_WaterFX;

            private class PassData
            {
                public RendererListHandle renderListHdl;
                public Color clearColor;
            }

            public WaterFxPass()
            {
                m_WaterFX = RTHandles.Alloc("_WaterFXMap", name: "_WaterFXMap");
                // only wanting to render transparent objects
                m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            }

            // Calling Configure since we are wanting to render into a RenderTexture and control cleat
            [ObsoleteAttribute] public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                // no need for a depth buffer
                cameraTextureDescriptor.depthBufferBits = 0;
                // Half resolution
                cameraTextureDescriptor.width /= 2;
                cameraTextureDescriptor.height /= 2;
                // default format TODO research usefulness of HDR format
                cameraTextureDescriptor.colorFormat = RenderTextureFormat.Default;
                // get a temp RT for rendering into
                cmd.GetTemporaryRT(Shader.PropertyToID(m_WaterFX.name), cameraTextureDescriptor, FilterMode.Bilinear);
                ConfigureTarget(m_WaterFX);
                // clear the screen with a specific color for the packed data
                ConfigureClear(ClearFlag.Color, m_ClearColor);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer contextContainer)
            {
                UniversalCameraData cameraData = contextContainer.Get<UniversalCameraData>();
                UniversalResourceData resourceData = contextContainer.Get<UniversalResourceData>();

                using (var builder = renderGraph.AddRasterRenderPass<PassData>(nameof(WaterCausticsPass), out var passData, profilingSampler))
                {
                    passData.clearColor = m_ClearColor;

                    UniversalRenderingData renderingData = contextContainer.Get<UniversalRenderingData>();
                    var renderListDesc = new RendererListDesc(m_WaterFXShaderTag, renderingData.cullResults, cameraData.camera)
                    {
                        sortingCriteria = SortingCriteria.CommonTransparent,
                        renderQueueRange = RenderQueueRange.transparent,
                    };
                    passData.renderListHdl = renderGraph.CreateRendererList(renderListDesc);
                    builder.UseRendererList(passData.renderListHdl);
                    
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawRendererList(data.renderListHdl);
                    });
                }
            }

            [ObsoleteAttribute] public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_WaterFX_Profile)) // makes sure we have profiling ability
                {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    // here we choose renderers based off the "WaterFX" shader pass and also sort back to front
                    var drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, ref renderingData,
                        SortingCriteria.CommonTransparent);

                    // draw all the renderers matching the rules we setup
                    context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public override void OnCameraCleanup(CommandBuffer cmd) 
            {
                // since the texture is used within the single cameras use we need to cleanup the RT afterwards
                cmd.ReleaseTemporaryRT(Shader.PropertyToID(m_WaterFX.name));
            }
        }

        #endregion

        #region Caustics Pass

        class WaterCausticsPass : ScriptableRenderPass
        {
            private const string k_RenderWaterCausticsTag = "Render Water Caustics";
            private ProfilingSampler m_WaterCaustics_Profile = new ProfilingSampler(k_RenderWaterCausticsTag);
            public Material WaterCausticMaterial;
            private static Mesh m_mesh;

            [ObsoleteAttribute]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cam = renderingData.cameraData.camera;
                // Stop the pass rendering in the preview or material missing
                if (cam.cameraType == CameraType.Preview || !WaterCausticMaterial)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_WaterCaustics_Profile))
                {
                    var sunMatrix = RenderSettings.sun != null
                         ? RenderSettings.sun.transform.localToWorldMatrix
                         : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
                    WaterCausticMaterial.SetMatrix("_MainLightDir", sunMatrix);

                    // Create mesh if needed
                    if (!m_mesh)
                        m_mesh = GenerateCausticsMesh(1000f);

                    // Create the matrix to position the caustics mesh.
                    var position = cam.transform.position;
                    position.y = 0; // TODO should read a global 'water height' variable.
                    var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                    // Setup the CommandBuffer and draw the mesh with the caustic material and matrix
                    cmd.DrawMesh(m_mesh, matrix, WaterCausticMaterial, 0, 0);

                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            private class PassData
            {
                public Vector3 cameraPosition;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer contextContainer)
            {
                UniversalCameraData cameraData = contextContainer.Get<UniversalCameraData>();
                UniversalResourceData resourceData = contextContainer.Get<UniversalResourceData>();

                using (var builder = renderGraph.AddRasterRenderPass<PassData>(nameof(WaterCausticsPass), out var passData, profilingSampler))
                {
                    passData = new PassData
                    {
                        cameraPosition = cameraData.worldSpaceCameraPos
                    };

                    // Stop the pass rendering in the preview and if material is missing
                    //if (!ExecutionCheck(camera, passData.data.WaterCausticMaterial)) return;

                    builder.AllowPassCulling(false);

                    // set buffers
                    //builder.SetRenderAttachment(resourceData.activeColorTexture, 0); // This causes errors, why ?
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);

                    // set depthtexture read for the shader
                    builder.UseTexture(resourceData.cameraDepthTexture);

                    var sunMatrix = RenderSettings.sun != null
                        ? RenderSettings.sun.transform.localToWorldMatrix
                        : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
                    WaterCausticMaterial.SetMatrix("_MainLightDir", sunMatrix);

                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        // Create mesh if needed
                        if (!m_mesh)
                            m_mesh = GenerateCausticsMesh(1000f);

                        if (m_mesh != null || WaterCausticMaterial != null)
                        {
                            // Create the matrix to position the caustics mesh.
                            var position = data.cameraPosition;
                            position.y = 0; // TODO should read a global 'water height' variable.
                            var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);

                            context.cmd.DrawMesh(m_mesh, matrix, WaterCausticMaterial, 0, 0);
                        }
                    });
                }
            }
        }

        #endregion

        WaterFxPass m_WaterFxPass;
        WaterCausticsPass m_CausticsPass;

        public WaterSystemSettings settings = new WaterSystemSettings();
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
            
            switch (settings.debug)
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
            }

            _causticMaterial.SetFloat(Size, settings.causticScale);
            m_CausticsPass.WaterCausticMaterial = _causticMaterial;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(m_WaterFxPass);
            renderer.EnqueuePass(m_CausticsPass);
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

        [System.Serializable]
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