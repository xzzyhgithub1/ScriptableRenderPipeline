using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using UnityEngine.VFX;
using Object = System.Object;

using Random = System.Random;
namespace UnityEditor.VFX.UI
{

    class VFXDebugUI
    {
        public enum Modes
        {
            None,
            Efficiency,
            Alive
        }

        public enum Events
        {
            VFXPlayPause,
            VFXStep,
            VFXReset,
            VFXStop
        }

        class CurveContent : ImmediateModeElement
        {

            class VerticalBar
            {
                Mesh m_Mesh;
                public VerticalBar(float xPos)
                {
                    m_Mesh = new Mesh();
                    m_Mesh.vertices = new Vector3[] { new Vector3(xPos, -1, 0), new Vector3(xPos, 2, 0) };
                    m_Mesh.SetIndices(new int[] { 0, 1 }, MeshTopology.Lines, 0);
                }

                public Mesh GetMesh()
                {
                    return m_Mesh;
                }
            }

            class NormalizedCurve
            {
                int m_MaxPoints;
                Mesh m_Mesh;

                Vector3[] m_Points;

                public NormalizedCurve(int maxPoints)
                {
                    if (maxPoints < 2)
                        maxPoints = 2;

                    m_MaxPoints = maxPoints;

                    // line output
                    m_Points = new Vector3[maxPoints];
                    var linesIndices = new int[2 * (maxPoints - 1)];

                    var step = 1.0f / (float)(maxPoints - 1);

                    for (int i = 0; i < maxPoints - 1; ++i)
                    {
                        m_Points[i] = new Vector3(i * step, -99999999, 0);
                        linesIndices[2 * i] = i;
                        linesIndices[2 * i + 1] = i + 1;
                    }

                    m_Points[m_Points.Length - 1] = new Vector3(1, 0, 0);

                    m_Mesh = new Mesh();
                    m_Mesh.vertices = m_Points;

                    m_Mesh.SetIndices(linesIndices, MeshTopology.Lines, 0);
                }

                public Mesh GetMesh()
                {
                    return m_Mesh;
                }


                public void AddPoint(float value)
                {
                    m_Points = m_Mesh.vertices;
                    for (int i = 1; i < m_MaxPoints; ++i)
                        m_Points[i - 1].y = m_Points[i].y;

                    m_Points[m_Points.Length - 1].y = value;

                    m_Mesh.vertices = m_Points;
                }

                public float GetMax()
                {
                    float max = m_Points[0].y;
                    foreach (var point in m_Points)
                    {
                        if (max < point.y)
                            max = point.y;
                    }

                    return max;
                }
            }

            class SwitchableCurve
            {
                int m_MaxPoints;
                Toggle m_Toggle;
                //Button m_MaxAlive;

                public NormalizedCurve curve { get; set; }
                public int id { get; set; }
                public Toggle toggle
                {
                    get { return m_Toggle; }
                    set
                    {
                        if (m_Toggle != value && m_Toggle != null)
                            m_Toggle.UnregisterValueChangedCallback(ToggleValueChanged);
                        m_Toggle = value;
                    }
                }

                public SwitchableCurve(int id, int maxPoints, Toggle toggle)
                {
                    m_MaxPoints = maxPoints;
                    curve = new NormalizedCurve(m_MaxPoints);
                    this.id = id;
                    this.toggle = toggle;
                    if (this.toggle != null)
                        toggle.RegisterValueChangedCallback(ToggleValueChanged);
                }

                public void ResetCurve()
                {
                    curve = new NormalizedCurve(m_MaxPoints);
                }

                void ToggleValueChanged(ChangeEvent<bool> evt)
                {
                    if (evt.newValue == false)
                        curve = new NormalizedCurve(m_MaxPoints);
                }
            }

            Material m_CurveMat;
            Material m_BarMat;
            VFXDebugUI m_DebugUI;
            int m_ClippingMatrixId;

            List<SwitchableCurve> m_VFXCurves;
            VerticalBar m_VerticalBar;
            List<float> m_TimeBarsOffsets;

            int m_MaxPoints;
            float m_TimeBetweenDraw;
            bool m_Pause;
            bool m_Stopped;
            bool m_Step;
            bool m_ShouldDrawTimeBars = true;
            static readonly float s_TimeBarsInterval = 1;

            private static Func<VisualElement, Rect> GetWorldClipRect()
            {
                var worldClipProp = typeof(VisualElement).GetMethod("get_worldClip", BindingFlags.NonPublic | BindingFlags.Instance);
                if (worldClipProp != null)
                {
                    return delegate (VisualElement elt)
                    {
                        return (Rect)worldClipProp.Invoke(elt, null);
                    };
                }

                Debug.LogError("could not retrieve get_worldClip");
                return delegate (VisualElement elt)
                {
                    return new Rect();
                };
            }

            static readonly Func<Box, Rect> k_BoxWorldclip = GetWorldClipRect();

            public CurveContent(VFXDebugUI debugUI, int maxPoints, long timeBetweenDraw = 33)
            {
                m_DebugUI = debugUI;
                m_CurveMat = new Material(Shader.Find("Hidden/VFX/SystemStat"));
                m_BarMat = new Material(Shader.Find("Hidden/VFX/TimeBar"));
                m_ClippingMatrixId = Shader.PropertyToID("_ClipMatrix");
                m_MaxPoints = maxPoints;

                m_VerticalBar = new VerticalBar(0);
                m_TimeBarsOffsets = new List<float>();
                m_LastTimeBarDrawTime = -2 * s_TimeBarsInterval;

                SetSamplingRate((long)timeBetweenDraw);
            }

            public void SetSamplingRate(long rate)
            {
                //schedule.Execute(MarkDirtyRepaint).Every(rate);
                m_TimeBetweenDraw = rate / 1000.0f;
            }

            public void OnVFXChange()
            {
                if ((m_DebugUI.m_CurrentMode == Modes.Efficiency || m_DebugUI.m_CurrentMode == Modes.Alive) && m_DebugUI.m_VFX != null)
                {
                    m_VFXCurves = new List<SwitchableCurve>(m_DebugUI.m_GpuSystems.Count());
                    m_TimeBarsOffsets.Clear();
                    for (int i = 0; i < m_DebugUI.m_GpuSystems.Count(); ++i)
                    {
                        var toggle = m_DebugUI.m_SystemStats[m_DebugUI.m_GpuSystems[i]][1] as Toggle;
                        var switchableCurve = new SwitchableCurve(m_DebugUI.m_GpuSystems[i], m_MaxPoints, toggle);
                        m_VFXCurves.Add(switchableCurve);
                    }
                }
            }

            public void Notify(Events e)
            {
                switch (e)
                {
                    case Events.VFXPlayPause:
                        m_Pause = !m_Pause;
                        m_Stopped = false;
                        break;
                    case Events.VFXStep:
                        m_Step = true;
                        m_Pause = true;
                        m_Stopped = false;
                        break;
                    case Events.VFXReset:
                        foreach (var curve in m_VFXCurves)
                            curve.ResetCurve();
                        m_TimeBarsOffsets.Clear();
                        break;
                    case Events.VFXStop:
                        m_Pause = true;
                        m_Stopped = true;
                        foreach (var curve in m_VFXCurves)
                            curve.ResetCurve();
                        m_TimeBarsOffsets.Clear();
                        break;
                    default:
                        break;
                }
            }

            public void SetDrawTimeBars(bool status)
            {
                m_ShouldDrawTimeBars = status;
            }

            Object GetCurvesData()
            {
                switch (m_DebugUI.m_CurrentMode)
                {
                    case Modes.Efficiency:
                        return null;
                    case Modes.Alive:
                        {
                            float max = -1;
                            foreach (var switchableCurve in m_VFXCurves)
                            {
                                max = Mathf.Max(switchableCurve.curve.GetMax(), max);
                            }
                            return max;
                        }
                    default:
                        return null;
                }
            }

            void UpdateCurve(SwitchableCurve switchableCurve, Object data)
            {
                switch (m_DebugUI.m_CurrentMode)
                {
                    case Modes.Efficiency:
                        {
                            var stat = m_DebugUI.m_VFX.GetParticleSystemStat(switchableCurve.id);
                            float efficiency = (float)stat.alive / (float)stat.capacity;

                            m_CurveMat.SetFloat("_OrdinateScale", 1.0f);
                            switchableCurve.curve.AddPoint(efficiency);
                            m_DebugUI.UpdateStatEntry(switchableCurve.id, stat);
                        }
                        break;
                    case Modes.Alive:
                        {
                            var stat = m_DebugUI.m_VFX.GetParticleSystemStat(switchableCurve.id);
                            float maxAlive = (float)data;

                            int superior2 = (int)Mathf.Pow(2, Mathf.CeilToInt(Mathf.Log(maxAlive, 2.0f)));
                            m_DebugUI.m_YaxisElts[1].text = (superior2 / 2).ToString();
                            m_DebugUI.m_YaxisElts[2].text = superior2.ToString();

                            m_CurveMat.SetFloat("_OrdinateScale", 1.0f / (float)superior2);
                            switchableCurve.curve.AddPoint(stat.alive);
                            m_DebugUI.UpdateStatEntry(switchableCurve.id, stat);

                        }
                        break;
                    default:
                        break;
                }
            }

            float m_LastSampleTime;
            float m_LastTimeBarDrawTime;
            void DrawCurves()
            {
                if (m_Stopped)
                    return;

                MarkDirtyRepaint();

                if (m_CurveMat == null)
                {
                    m_CurveMat = new Material(Shader.Find("Hidden/VFX/SystemStat"));
                    m_BarMat = new Material(Shader.Find("Hidden/VFX/VerticalBar"));
                    m_ClippingMatrixId = Shader.PropertyToID("_ClipMatrix");
                }
                // drawing matrix
                var debugRect = m_DebugUI.m_DebugDrawingBox.worldBound;
                var clippedDebugRect = k_BoxWorldclip(m_DebugUI.m_DebugDrawingBox);
                var windowRect = panel.InternalGetGUIView().position;
                var trans = new Vector4(debugRect.x / windowRect.width, (windowRect.height - (debugRect.y + debugRect.height)) / windowRect.height, 0, 0);
                var scale = new Vector3(debugRect.width / windowRect.width, debugRect.height / windowRect.height, 0);

                // clipping matrix
                var clippedScale = new Vector3(windowRect.width / clippedDebugRect.width, windowRect.height / clippedDebugRect.height, 0);
                var clippedTrans = new Vector3(-clippedDebugRect.x / clippedDebugRect.width, ((clippedDebugRect.y + clippedDebugRect.height) - windowRect.height) / clippedDebugRect.height);
                var baseChange = Matrix4x4.TRS(clippedTrans, Quaternion.identity, clippedScale);
                m_CurveMat.SetMatrix(m_ClippingMatrixId, baseChange);
                m_BarMat.SetMatrix(m_ClippingMatrixId, baseChange);


                // Updating curve
                var now = Time.time;
                bool shouldSample = (!m_Pause && (now - m_LastSampleTime > m_TimeBetweenDraw)) || (m_Pause && m_Step);
                m_Step = false;
                if (shouldSample)
                {
                    m_LastSampleTime = now;
                }

                int i = 0;
                var curveData = GetCurvesData();
                var TRS = Matrix4x4.TRS(trans, Quaternion.identity, scale);
                foreach (var vfxCurve in m_VFXCurves)
                {
                    if (vfxCurve.toggle == null || vfxCurve.toggle.value == true)
                    {
                        if (shouldSample && m_DebugUI.m_VFX.HasSystem(vfxCurve.id))
                        {
                            UpdateCurve(vfxCurve, curveData);
                        }

                        var curveColor = Color.HSVToRGB((0.71405f + i * 0.37135766f) % 1.0f, 0.6f, 1.0f).gamma;
                        m_CurveMat.SetColor("_Color", curveColor);

                        m_CurveMat.SetPass(0);
                        Graphics.DrawMeshNow(vfxCurve.curve.GetMesh(), TRS);
                    }

                    ++i;
                }

                // time bars creation
                if (shouldSample && (now - m_LastTimeBarDrawTime > s_TimeBarsInterval))
                {
                    m_LastTimeBarDrawTime = now;
                    m_TimeBarsOffsets.Add(1);
                }

                // time bars update
                //m_TimeBarsOffsets.RemoveAll(timeBar => timeBar < 0);
                var xShift = 1.0f / (float)(m_MaxPoints - 1);
                Color timeBarColor = new Color(1, 0, 0, 0.5f).gamma;
                for (int j = 0; j < m_TimeBarsOffsets.Count(); ++j)
                {
                    if (m_TimeBarsOffsets[j] < 0)
                    {
                        m_TimeBarsOffsets.RemoveAt(j);
                        --j;
                        continue;
                    }
                    m_BarMat.SetFloat("_AbscissaOffset", m_TimeBarsOffsets[j]);
                    if (shouldSample)
                        m_TimeBarsOffsets[j] -= xShift;

                    if (m_ShouldDrawTimeBars)
                    {
                        m_BarMat.SetColor("_Color", timeBarColor);
                        m_BarMat.SetPass(0);
                        Graphics.DrawMeshNow(m_VerticalBar.GetMesh(), TRS);
                    }
                }

                m_BarMat.SetFloat("_AbscissaOffset", 0);
            }


            protected override void ImmediateRepaint()
            {
                DrawCurves();
            }
        }

        Modes m_CurrentMode;

        // graph characteristics
        VFXView m_View;
        VisualEffect m_VFX;
        List<int> m_GpuSystems;

        // debug components
        VFXComponentBoard m_ComponentBoard;
        VisualElement m_DebugContainer;
        Button m_DebugButton;
        VisualElement m_SystemStatsContainer;
        Box m_DebugDrawingBox;
        CurveContent m_Curves;

        // [0] container
        // [1] toggle
        // [2] system name
        // [3] alive
        // [4] max alive (Button)
        // [5] efficiency
        Dictionary<int, VisualElement[]> m_SystemStats;
        // [0] bottom value
        // [1] mid value
        // [2] top value
        TextElement[] m_YaxisElts;

        public VFXDebugUI(VFXView view)
        {
            m_View = view;
        }

        ~VFXDebugUI()
        {
            Clear();
        }

        public void SetDebugMode(Modes mode, VFXComponentBoard componentBoard, bool force = false)
        {
            if (mode == m_CurrentMode && !force)
                return;

            ClearDebugMode();
            m_CurrentMode = mode;

            m_ComponentBoard = componentBoard;
            m_DebugContainer = m_ComponentBoard.Query<VisualElement>("debug-modes-container");
            m_DebugButton = m_ComponentBoard.Query<Button>("debug-modes");

            switch (m_CurrentMode)
            {
                case Modes.Efficiency:
                    m_View.controller.RegisterNotification(m_View.controller.graph, UpdateDebugMode);
                    Efficiency();
                    break;
                case Modes.Alive:
                    m_View.controller.RegisterNotification(m_View.controller.graph, UpdateDebugMode);
                    Alive();
                    break;
                case Modes.None:
                    None();
                    Clear();
                    break;
                default:
                    Clear();
                    break;
            }
        }

        void UpdateDebugMode()
        {
            switch (m_CurrentMode)
            {
                case Modes.Efficiency:
                    RegisterParticleSystems();
                    InitStatArray();
                    break;
                case Modes.Alive:
                    RegisterParticleSystems();
                    InitStatArray();
                    break;
                default:
                    break;
            }
        }

        void ClearDebugMode()
        {
            switch (m_CurrentMode)
            {
                case Modes.Efficiency:
                    m_View.controller.UnRegisterNotification(m_View.controller.graph, UpdateDebugMode);
                    Clear();
                    break;
                case Modes.Alive:
                    m_View.controller.UnRegisterNotification(m_View.controller.graph, UpdateDebugMode);
                    Clear();
                    break;
                default:
                    break;
            }
        }


        public void SetVisualEffect(VisualEffect vfx)
        {
            m_VFX = vfx;

            if (m_Curves != null)
                m_Curves.OnVFXChange();
        }

        public void Notify(Events e)
        {
            switch (e)
            {
                case Events.VFXReset:
                    InitStatArray();
                    break;
                case Events.VFXStop:
                    InitStatArray();
                    break;
                default:
                    break;
            }
            m_Curves.Notify(e);
        }

        void RegisterParticleSystems()
        {
            if (m_SystemStats != null)
                foreach (var systemStat in m_SystemStats.Values)
                    m_SystemStatsContainer.Remove(systemStat[0]);

            m_SystemStats = new Dictionary<int, VisualElement[]>();
            if (m_VFX != null)
            {
                List<string> particleSystemNames = new List<string>();
                m_VFX.GetParticleSystemNames(particleSystemNames);
                m_GpuSystems = new List<int>();
                int i = 0;
                foreach (var name in particleSystemNames)
                {
                    int id = Shader.PropertyToID(name);
                    m_GpuSystems.Add(id);
                    AddSystemStatEntry(name, id, Color.HSVToRGB((0.71405f + i * 0.37135766f) % 1.0f, 0.6f, 1.0f));

                    ++i;
                }

                m_Curves.OnVFXChange();
            }
        }

        void ToggleAll(ChangeEvent<bool> evt)
        {
            foreach (var systemStat in m_SystemStats.Values)
            {
                var toggle = systemStat[1] as Toggle;
                if (toggle != null)
                    toggle.value = evt.newValue;
            }
        }

        void None()
        {
            m_DebugButton.text = "Debug modes";
        }

        void Efficiency()
        {
            // ui
            m_DebugButton.text = "Efficiency Plot";
            m_Curves = new CurveContent(this, (int)(10.0f / 0.016f), 16);
            m_ComponentBoard.contentContainer.Add(m_Curves);

            var Yaxis = SetYAxis("100%", "50%", "0%");
            m_DebugDrawingBox = SetDebugDrawingBox();

            var settingsBox = SetSettingsBox();

            var plotArea = SetPlotArea(m_DebugDrawingBox, Yaxis);

            var title = SetStatsTitle();

            m_SystemStatsContainer = SetSystemStatContainer();

            m_DebugContainer.Add(settingsBox);
            m_DebugContainer.Add(plotArea);
            m_DebugContainer.Add(title);
            m_DebugContainer.Add(m_SystemStatsContainer);

            // recover debug data
            RegisterParticleSystems();
        }


        void Alive()
        {
            // ui
            m_DebugButton.text = "Alive Particles Count Plot";
            m_Curves = new CurveContent(this, (int)(10.0f / 0.016f), 16);
            m_ComponentBoard.contentContainer.Add(m_Curves);

            var Yaxis = SetYAxis("", "", "0");
            m_DebugDrawingBox = SetDebugDrawingBox();

            var settingsBox = SetSettingsBox();

            var plotArea = SetPlotArea(m_DebugDrawingBox, Yaxis);

            var title = SetStatsTitle();

            m_SystemStatsContainer = SetSystemStatContainer();

            m_DebugContainer.Add(settingsBox);
            m_DebugContainer.Add(plotArea);
            m_DebugContainer.Add(title);
            m_DebugContainer.Add(m_SystemStatsContainer);

            // recover debug data
            RegisterParticleSystems();
        }

        VisualElement SetSettingsBox()
        {
            // sampling rate
            var labelSR = new Label();
            labelSR.text = "Sampling rate (ms)";
            labelSR.style.fontSize = 12;
            var fieldSR = new IntegerField();
            fieldSR.value = 16;
            fieldSR.RegisterValueChangedCallback(SetSampleRate);
            var containerSR = new VisualElement();
            containerSR.name = "debug-settings-element-container";
            containerSR.Add(labelSR);
            containerSR.Add(fieldSR);

            // time bars toggle
            var labelTB = new Label();
            labelTB.text = "Toggle time bars";
            labelTB.style.fontSize = 12;
            var toggleTB = new Toggle();
            toggleTB.RegisterValueChangedCallback(ToggleTimeBars);
            toggleTB.value = true;
            toggleTB.style.justifyContent = Justify.Center;
            var containerTB = new VisualElement();
            containerTB.name = "debug-settings-element-container";
            containerTB.Add(labelTB);
            containerTB.Add(toggleTB);

            var settingsContainer = new VisualElement();
            settingsContainer.name = "debug-settings-container";
            settingsContainer.Add(containerSR);
            settingsContainer.Add(containerTB);
            return settingsContainer;
        }

        void SetSampleRate(ChangeEvent<int> e)
        {
            var intergerField = e.currentTarget as IntegerField;
            if (intergerField != null)
            {
                if (e.newValue < 1)
                    intergerField.value = 1;

                m_Curves.SetSamplingRate(intergerField.value);
            }
        }

        void ToggleTimeBars(ChangeEvent<bool> e)
        {
            var toggle = e.currentTarget as Toggle;
            if (toggle != null)
            {
                m_Curves.SetDrawTimeBars(e.newValue);
            }
        }

        VisualElement SetYAxis(string topValue, string midValue, string botValue)
        {
            var Yaxis = new VisualElement();
            Yaxis.name = "debug-box-axis-container";
            var top = new TextElement();
            top.text = topValue;
            top.name = "debug-box-axis-100";
            var mid = new TextElement();
            mid.text = midValue;
            mid.name = "debug-box-axis-50";
            var bot = new TextElement();
            bot.text = botValue;
            bot.name = "debug-box-axis-0";
            Yaxis.Add(top);
            Yaxis.Add(mid);
            Yaxis.Add(bot);

            m_YaxisElts = new TextElement[3];
            m_YaxisElts[0] = bot;
            m_YaxisElts[1] = mid;
            m_YaxisElts[2] = top;

            return Yaxis;
        }

        Box SetDebugDrawingBox()
        {
            var debugBox = new Box();
            debugBox.name = "debug-box";
            return debugBox;
        }

        VisualElement SetPlotArea(Box debugDrawingBox, VisualElement Yaxis)
        {
            var plotArea = new VisualElement();
            plotArea.name = "debug-plot-area";

            plotArea.Add(debugDrawingBox);
            plotArea.Add(Yaxis);

            return plotArea;
        }

        VisualElement SetSystemStatContainer()
        {
            var scrollerContainer = new ScrollView();
            scrollerContainer.name = "debug-system-stat-container";
            return scrollerContainer;
        }

        VisualElement SetStatsTitle()
        {
            var toggleAll = new Toggle();
            toggleAll.value = true;
            toggleAll.RegisterValueChangedCallback(ToggleAll);

            var systemStatName = new TextElement();
            systemStatName.name = "debug-system-stat-title-name";
            systemStatName.text = "Particle System";

            var systemStatAlive = new TextElement();
            systemStatAlive.name = "debug-system-stat-title";
            systemStatAlive.text = "Alive";

            var systemStatCapacity = new TextElement();
            systemStatCapacity.name = "debug-system-stat-title";
            systemStatCapacity.text = "Max Alive";

            var systemStatEfficiency = new TextElement();
            systemStatEfficiency.name = "debug-system-stat-title";
            systemStatEfficiency.text = "Efficiency";

            var titleContainer = new VisualElement();
            titleContainer.name = "debug-system-stat-entry-container";

            titleContainer.Add(toggleAll);
            titleContainer.Add(systemStatName);
            titleContainer.Add(systemStatAlive);
            titleContainer.Add(systemStatCapacity);
            titleContainer.Add(systemStatEfficiency);

            return titleContainer;
        }

        void AddSystemStatEntry(string systemName, int id, Color color)
        {
            var statContainer = new VisualElement();
            statContainer.name = "debug-system-stat-entry-container";
            m_SystemStatsContainer.Add(statContainer);

            var toggle = new Toggle();
            toggle.value = true;

            var name = new TextElement();
            name.name = "debug-system-stat-entry-name";
            name.text = systemName;
            name.style.color = color;

            var alive = new TextElement();
            alive.name = "debug-system-stat-entry";
            alive.text = " - ";

            var maxAlive = new Button();
            maxAlive.name = "debug-system-stat-entry";
            maxAlive.text = "0";
            maxAlive.clickable.clickedWithEventInfo += CapacitySetter(systemName);
            //maxAlive.clickable.clicked
            //maxAlive.tooltip = "Set the capacity of this particle system to this value";

            var efficiency = new TextElement();
            efficiency.name = "debug-system-stat-entry";
            efficiency.text = " - ";

            statContainer.Add(toggle);
            statContainer.Add(name);
            statContainer.Add(alive);
            statContainer.Add(maxAlive);
            statContainer.Add(efficiency);

            var stats = new VisualElement[6];
            stats[0] = statContainer;
            stats[1] = toggle;
            stats[2] = name;
            stats[3] = alive;
            stats[4] = maxAlive;
            stats[5] = efficiency;

            m_SystemStats[id] = stats;
        }

        Action<EventBase> CapacitySetter(string systemName)
        {
            var graph = m_View.controller.graph;
            var models = new HashSet<ScriptableObject>();
            graph.CollectDependencies(models, false);
            var datas = models.OfType<VFXDataParticle>();

            foreach (var data in datas)
            {
                if (graph.systemNames.GetUniqueSystemName(data) == systemName)
                    return (e) =>
                    {
                        var button = e.currentTarget as Button;
                        if (button != null)
                            data.SetSettingValue("capacity", (uint)(float.Parse(button.text) * 1.05f));
                    };
            }
            return (e) => { };
        }

        void UpdateStatEntry(int systemId, VFXSystemStat stat)
        {
            var statUI = m_SystemStats[systemId];// [0] is title bar
            if (statUI[3] is TextElement alive)
                alive.text = stat.alive.ToString();
            if (statUI[4] is Button maxAlive)
                maxAlive.text = Mathf.Max(int.Parse(maxAlive.text), stat.alive).ToString();
            if (statUI[5] is TextElement efficiency)
                efficiency.text = string.Format("{0} %", (int)((float)stat.alive * 100.0f / (float)stat.capacity));
        }

        void InitStatArray()
        {
            if (m_SystemStats != null)
                foreach (var statUI in m_SystemStats.Values)
                {
                    if (statUI[3] is TextElement alive)
                        alive.text = " - ";
                    if (statUI[4] is Button maxAlive)
                        maxAlive.text = "0";
                    if (statUI[5] is TextElement efficiency)
                        efficiency.text = " - ";
                }
        }

        public void Clear()
        {
            if (m_ComponentBoard != null && m_Curves != null)
                m_ComponentBoard.contentContainer.Remove(m_Curves);
            m_ComponentBoard = null;
            m_Curves = null;

            if (m_SystemStatsContainer != null)
                m_SystemStatsContainer.Clear();

            m_YaxisElts = null;

            if (m_DebugContainer != null)
                m_DebugContainer.Clear();


            m_SystemStats = null;
            m_DebugDrawingBox = null;
            m_SystemStatsContainer = null;
            m_DebugContainer = null;
        }



    }
}
