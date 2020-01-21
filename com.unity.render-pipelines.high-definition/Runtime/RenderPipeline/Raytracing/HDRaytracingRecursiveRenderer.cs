using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // The kernel that allows us to override the color buffer
        Material m_RaytracingFlagMaterial = null;

        // String values
        const string m_RayGenShaderName = "RayGenRenderer";

        // Pass name for the flag pass
        ShaderTagId raytracingPassID = new ShaderTagId("Forward");
        RenderStateBlock m_RaytracingFlagStateBlock;

        void InitRecursiveRenderer()
        {
            m_RaytracingFlagStateBlock = new RenderStateBlock
            {
                depthState = new DepthState(false, CompareFunction.LessEqual),
                mask = RenderStateMask.Depth
            };
        }

        void ReleaseRecursiveRenderer()
        {
            if (m_RaytracingFlagMaterial != null)
            {
                CoreUtils.Destroy(m_RaytracingFlagMaterial);
            }
        }

        void EvaluateRaytracingMask(CullingResults cull, HDCamera hdCamera, CommandBuffer cmd, ScriptableRenderContext renderContext, RTHandle flagBuffer)
        {
            // Clear our target
            CoreUtils.SetRenderTarget(cmd, flagBuffer, ClearFlag.Color, Color.black);

            // Bind out custom color texture
            CoreUtils.SetRenderTarget(cmd, flagBuffer, m_SharedRTManager.GetDepthStencilBuffer());

            // This is done here because DrawRenderers API lives outside command buffers so we need to make call this before doing any DrawRenders
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var sortingSettings = new SortingSettings(hdCamera.camera)
            {
                criteria = 0
            };

            var filterSettings = new FilteringSettings(HDRenderQueue.k_RenderQueue_OpaqueRayTracing)
            {
                excludeMotionVectorObjects = false
            };

            var drawSettings = new DrawingSettings(HDShaderPassNames.s_EmptyName, sortingSettings)
            {
                perObjectData = 0
            };

            // First let's render the opaque objects
            m_RaytracingFlagMaterial.renderQueue = (int)HDRenderQueue.Priority.OpaqueRayTracing;
            drawSettings.SetShaderPassName(0, raytracingPassID);
            drawSettings.overrideMaterial = m_RaytracingFlagMaterial;
            drawSettings.overrideMaterialPassIndex = 0;
            renderContext.DrawRenderers(cull, ref drawSettings, ref filterSettings);

            // Set the render queue range for the transparent set
            filterSettings.renderQueueRange = HDRenderQueue.k_RenderQueue_PreRefractionRayTracing;
            // Then let's render the transparent objects
            m_RaytracingFlagMaterial.renderQueue = (int)HDRenderQueue.Priority.PreRefractionRayTracing;
            drawSettings.SetShaderPassName(0, raytracingPassID);
            drawSettings.overrideMaterial = m_RaytracingFlagMaterial;
            drawSettings.overrideMaterialPassIndex = 0;
            renderContext.DrawRenderers(cull, ref drawSettings, ref filterSettings);

            // Set the render queue range for the transparent set
            filterSettings.renderQueueRange = HDRenderQueue.k_RenderQueue_TransparentRayTracing;
            // Then let's render the transparent objects
            m_RaytracingFlagMaterial.renderQueue = (int)HDRenderQueue.Priority.TransparentRayTracing;
            drawSettings.SetShaderPassName(0, raytracingPassID);
            drawSettings.overrideMaterial = m_RaytracingFlagMaterial;
            drawSettings.overrideMaterialPassIndex = 0;
            renderContext.DrawRenderers(cull, ref drawSettings, ref filterSettings);

            // Set the render queue range for the transparent set
            filterSettings.renderQueueRange = HDRenderQueue.k_RenderQueue_LowTransparentRayTracing;
            // Then let's render the transparent objects
            m_RaytracingFlagMaterial.renderQueue = (int)HDRenderQueue.Priority.LowTransparentRayTracing;
            drawSettings.SetShaderPassName(0, raytracingPassID);
            drawSettings.overrideMaterial = m_RaytracingFlagMaterial;
            drawSettings.overrideMaterialPassIndex = 0;
            renderContext.DrawRenderers(cull, ref drawSettings, ref filterSettings);
        }

        bool RecursiveRenderingActive(HDCamera hdCamera)
        {
            // First thing to check is: Do we have a valid ray-tracing environment?
            RecursiveRendering recursiveSettings = hdCamera.volumeStack.GetComponent<RecursiveRendering>();

            // Check the validity of the state before computing the effect
            return hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing)
                 && recursiveSettings.enable.value
                 && m_Asset.currentPlatformRenderPipelineSettings.supportedRaytracingTier == RenderPipelineSettings.RaytracingTier.Tier2;
        }

        void RaytracingRecursiveRender(HDCamera hdCamera, CommandBuffer cmd, ScriptableRenderContext renderContext, CullingResults cull)
        {
            // First thing to check is: Do we have a valid ray-tracing environment?
            RecursiveRendering recursiveSettings = hdCamera.volumeStack.GetComponent<RecursiveRendering>();

            // Check the validity of the state before computing the effect
            bool invalidState = !hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing)
                || !recursiveSettings.enable.value
                || m_Asset.currentPlatformRenderPipelineSettings.supportedRaytracingTier == RenderPipelineSettings.RaytracingTier.Tier1;

            // If any resource or game-object is missing We stop right away
            if (invalidState)
                return;

            RayTracingShader forwardShader = m_Asset.renderPipelineRayTracingResources.forwardRaytracing;
            Shader raytracingMask = m_Asset.renderPipelineRayTracingResources.raytracingFlagMask;
            LightCluster lightClusterSettings = hdCamera.volumeStack.GetComponent<LightCluster>();
            RayTracingSettings rtSettings = hdCamera.volumeStack.GetComponent<RayTracingSettings>();

            // Grab the acceleration structure and the list of HD lights for the target camera
            RayTracingAccelerationStructure accelerationStructure = RequestAccelerationStructure();
            HDRaytracingLightCluster lightCluster = RequestLightCluster();

            // Fecth the temporary buffers we shall be using
            RTHandle flagBuffer = GetRayTracingBuffer(InternalRayTracingBuffers.R0);

            if (m_RaytracingFlagMaterial == null)
                m_RaytracingFlagMaterial = CoreUtils.CreateEngineMaterial(raytracingMask);

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.RayTracingRecursiveRendering)))
            {
                // Before going into ray tracing, we need to flag which pixels needs to be ray tracing
                EvaluateRaytracingMask(cull, hdCamera, cmd, renderContext, flagBuffer);

                // Define the shader pass to use for the reflection pass
                cmd.SetRayTracingShaderPass(forwardShader, "ForwardDXR");

                // Set the acceleration structure for the pass
                cmd.SetRayTracingAccelerationStructure(forwardShader, HDShaderIDs._RaytracingAccelerationStructureName, accelerationStructure);

                // Inject the ray-tracing sampling data
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._OwenScrambledTexture, m_Asset.renderPipelineResources.textures.owenScrambledRGBATex);
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._ScramblingTexture, m_Asset.renderPipelineResources.textures.scramblingTex);

                // Inject the ray generation data
                cmd.SetGlobalFloat(HDShaderIDs._RaytracingRayBias, rtSettings.rayBias.value);
                cmd.SetGlobalFloat(HDShaderIDs._RaytracingRayMaxLength, recursiveSettings.rayLength.value);
                cmd.SetGlobalFloat(HDShaderIDs._RaytracingMaxRecursion, recursiveSettings.maxDepth.value);
                cmd.SetGlobalFloat(HDShaderIDs._RaytracingCameraNearPlane, hdCamera.camera.nearClipPlane);

                // Set the data for the ray generation
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._RaytracingFlagMask, flagBuffer);
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._CameraColorTextureRW, m_CameraColorBuffer);

                // Set ray count texture
                RayCountManager rayCountManager = GetRayCountManager();
                cmd.SetRayTracingIntParam(forwardShader, HDShaderIDs._RayCountEnabled, rayCountManager.RayCountIsEnabled());
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._RayCountTexture, rayCountManager.GetRayCountTexture());

                // Compute an approximate pixel spread angle value (in radians)
                cmd.SetGlobalFloat(HDShaderIDs._RaytracingPixelSpreadAngle, GetPixelSpreadAngle(hdCamera.camera.fieldOfView, hdCamera.actualWidth, hdCamera.actualHeight));

                // LightLoop data
                cmd.SetGlobalBuffer(HDShaderIDs._RaytracingLightCluster, lightCluster.GetCluster());
                cmd.SetGlobalBuffer(HDShaderIDs._LightDatasRT, lightCluster.GetLightDatas());
                cmd.SetGlobalVector(HDShaderIDs._MinClusterPos, lightCluster.GetMinClusterPos());
                cmd.SetGlobalVector(HDShaderIDs._MaxClusterPos, lightCluster.GetMaxClusterPos());
                cmd.SetGlobalInt(HDShaderIDs._LightPerCellCount, lightClusterSettings.maxNumLightsPercell.value);
                cmd.SetGlobalInt(HDShaderIDs._PunctualLightCountRT, lightCluster.GetPunctualLightCount());
                cmd.SetGlobalInt(HDShaderIDs._AreaLightCountRT, lightCluster.GetAreaLightCount());

                // Note: Just in case, we rebind the directional light data (in case they were not)
                cmd.SetGlobalBuffer(HDShaderIDs._DirectionalLightDatas, m_LightLoopLightData.directionalLightData);
                cmd.SetGlobalInt(HDShaderIDs._DirectionalLightCount, m_lightList.directionalLights.Count);

                // Set the data for the ray miss
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._SkyTexture, m_SkyManager.GetSkyReflection(hdCamera));

                // If this is the right debug mode and we have at least one light, write the first shadow to the de-noised texture
                RTHandle debugBuffer = GetRayTracingBuffer(InternalRayTracingBuffers.RGBA0);
                cmd.SetRayTracingTextureParam(forwardShader, HDShaderIDs._RaytracingPrimaryDebug, debugBuffer);

                // Run the computation
                cmd.DispatchRays(forwardShader, m_RayGenShaderName, (uint)hdCamera.actualWidth, (uint)hdCamera.actualHeight, (uint)hdCamera.viewCount);

                HDRenderPipeline hdrp = (RenderPipelineManager.currentPipeline as HDRenderPipeline);
                hdrp.PushFullScreenDebugTexture(hdCamera, cmd, debugBuffer, FullScreenDebugMode.RecursiveRayTracing);
            }
        }
    }
}
