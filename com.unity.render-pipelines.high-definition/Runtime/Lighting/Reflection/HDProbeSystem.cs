using System;
using System.Collections.Generic;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    internal static class HDProbeSystem
    {
        static HDProbeSystemInternal s_Instance = new HDProbeSystemInternal();

        public static IList<HDProbe> realtimeViewDependentProbes { get { return s_Instance.realtimeViewDependentProbes; } }
        public static IList<HDProbe> realtimeViewIndependentProbes { get { return s_Instance.realtimeViewIndependentProbes; } }
        public static IList<HDProbe> bakedProbes { get { return s_Instance.bakedProbes; } }

        public static void RegisterProbe(HDProbe probe)
        {
            s_Instance.RegisterProbe(probe);
        }

        public static void UnregisterProbe(HDProbe probe)
        {
            s_Instance.UnregisterProbe(probe);
        }

        public static void RenderAndUpdateRenderData(
            IEnumerable<HDProbe> probes,
            Transform viewerTransform,
            ProbeSettings.Mode targetMode
        )
        {
            foreach (var probe in probes)
                RenderAndUpdateRenderData(probe, viewerTransform, targetMode);
        }

        public static void RenderAndUpdateRenderData(
            HDProbe probe, Transform viewerTransform, ProbeSettings.Mode targetMode
        )
        {
            var target = CreateAndSetRenderTargetIfRequired(probe, targetMode);
            var positionSettings = ProbeCapturePositionSettings.ComputeFrom(probe, viewerTransform);
            Matrix4x4 projectionMatrix, worldToCameraRHSMatrix;
            HDRenderUtilities.Render(
                probe.settings,
                positionSettings,
                target,
                out worldToCameraRHSMatrix, out projectionMatrix
            );

            var renderData = new PlanarReflectionProbe.RenderData
            {
                projectionMatrix = projectionMatrix,
                worldToCameraRHS = worldToCameraRHSMatrix
            };
            AssignRenderData(probe, renderData, targetMode);
        }

        public static void AssignTexture(HDProbe probe, Texture texture, ProbeSettings.Mode targetMode)
        {
            switch (probe.settings.type)
            {
                case ProbeSettings.ProbeType.ReflectionProbe:
                    {
                        var reflectionProbe = probe.GetComponent<ReflectionProbe>();
                        switch (targetMode)
                        {
                            case ProbeSettings.Mode.Baked: reflectionProbe.bakedTexture = texture; break;
                            case ProbeSettings.Mode.Custom: reflectionProbe.customBakedTexture = texture; break;
                            case ProbeSettings.Mode.Realtime: reflectionProbe.realtimeTexture = (RenderTexture)texture; break;
                        }
                        break;
                    }
                case ProbeSettings.ProbeType.PlanarProbe:
                    {
                        var planarProbe = (PlanarReflectionProbe)probe;
                        switch (targetMode)
                        {
                            case ProbeSettings.Mode.Baked: planarProbe.bakedTexture = texture; break;
                            case ProbeSettings.Mode.Custom: planarProbe.customTexture = texture; break;
                            case ProbeSettings.Mode.Realtime: planarProbe.realtimeTexture = (RenderTexture)texture; break;
                        }
                        break;
                    }
            }
        }

        public static void AssignRenderData(
            HDProbe probe,
            PlanarReflectionProbe.RenderData renderData,
            ProbeSettings.Mode targetMode
        )
        {
            var planarProbe = probe as PlanarReflectionProbe;
            if (planarProbe == null)
                return;

            switch (targetMode)
            {
                case ProbeSettings.Mode.Baked: planarProbe.bakedRenderData = renderData; break;
                case ProbeSettings.Mode.Custom: planarProbe.customRenderData = renderData; break;
                case ProbeSettings.Mode.Realtime: planarProbe.realtimeRenderData = renderData; break;
            }
        }

        static Texture GetTexture(HDProbe probe, ProbeSettings.Mode targetMode)
        {
            
        }

        static Texture CreateAndSetRenderTargetIfRequired(HDProbe probe, ProbeSettings.Mode targetMode)
        {
            var settings = probe.settings;
            Texture target = null;



            if (probe.realtimeTexture != null)
                return;

            var hd = (HDRenderPipeline)RenderPipelineManager.currentPipeline;
            switch (settings.type)
            {
                case ProbeSettings.ProbeType.PlanarProbe:
                    probe.realtimeTexture = HDRenderUtilities.CreatePlanarProbeRenderTarget(
                        (int)hd.renderPipelineSettings.lightLoopSettings.planarReflectionTextureSize
                    );
                    break;
                case ProbeSettings.ProbeType.ReflectionProbe:
                    probe.realtimeTexture = HDRenderUtilities.CreateReflectionProbeRenderTarget(
                        (int)hd.renderPipelineSettings.lightLoopSettings.reflectionCubemapSize
                    );
                    break;
            }
        }
    }

    class HDProbeSystemInternal
    {
        List<HDProbe> m_BakedProbes = new List<HDProbe>();
        List<HDProbe> m_RealtimeViewDependentProbes = new List<HDProbe>();
        List<HDProbe> m_RealtimeViewIndependentProbes = new List<HDProbe>();

        public IList<HDProbe> bakedProbes
        { get { RemoveDestroyedProbes(m_BakedProbes); return m_BakedProbes; } }
        public IList<HDProbe> realtimeViewDependentProbes
        { get { RemoveDestroyedProbes(m_RealtimeViewDependentProbes); return m_RealtimeViewDependentProbes; } }
        public IList<HDProbe> realtimeViewIndependentProbes
        { get { RemoveDestroyedProbes(m_RealtimeViewIndependentProbes); return m_RealtimeViewIndependentProbes; } }

        internal void RegisterProbe(HDProbe probe)
        {
            var settings = probe.settings;
            switch (settings.mode)
            {
                case ProbeSettings.Mode.Baked:
                    m_BakedProbes.Add(probe);
                    break;
                case ProbeSettings.Mode.Realtime:
                    switch (probe.settings.type)
                    {
                        case ProbeSettings.ProbeType.PlanarProbe:
                            m_RealtimeViewDependentProbes.Add(probe);
                            break;
                        case ProbeSettings.ProbeType.ReflectionProbe:
                            m_RealtimeViewIndependentProbes.Add(probe);
                            break;
                    }
                    break;
            }
        }

        internal void UnregisterProbe(HDProbe probe)
        {
            m_BakedProbes.Remove(probe);
        }

        void RemoveDestroyedProbes(List<HDProbe> probes)
        {
            for (int i = probes.Count - 1; i >= 0; --i)
            {
                if (probes[i] == null || probes[i].Equals(null))
                    probes.RemoveAt(i);
            }
        }
    }
}
