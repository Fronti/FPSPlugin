﻿using Dalamud.Game.Internal;
using Dalamud.Plugin;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;

namespace FPSPlugin {
    public class FPSPlugin : IDalamudPlugin {
        public string Name => "FPS Plugin";
        public DalamudPluginInterface PluginInterface { get; private set; }
        public FPSPluginConfig PluginConfig { get; private set; }

        private bool drawConfigWindow;
        private bool gameUIHidden;
        private bool chatHidden;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetBaseUIObjDelegate();

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetUI2ObjByNameDelegate(IntPtr getBaseUIObj, string uiName, int index = 1);

        private GetBaseUIObjDelegate getBaseUIObj;
        private GetUI2ObjByNameDelegate getUI2ObjByName;

        private delegate IntPtr ToggleUIDelegate(IntPtr baseAddress, byte unknownByte);

        private Hook<ToggleUIDelegate> toggleUIHook;

        private IntPtr chatLogObject = IntPtr.Zero;

        private List<float> fpsHistory;
        private Stopwatch fpsHistoryInterval;
        private string fpsText;
        private Vector2 windowSize = Vector2.One;

        private bool fontBuilt;
        private bool fontLoadFailed;
        private ImFontPtr font;
        private float maxSeenFps;

        private string setLocation = null;

        /// <summary>
        /// LivePluginLoader Helper. Tell the plugin where it is
        /// </summary>
        /// <param name="dllPath">Location of the Assembly</param>
        private void SetLocation(string dllPath)
        {
            setLocation = dllPath;     
        }

        private string Location => setLocation ?? Assembly.GetExecutingAssembly().Location;

        public void Dispose() {
            PluginInterface.UiBuilder.OnBuildUi -= this.BuildUI;
            PluginInterface.UiBuilder.OnBuildFonts -= this.BuildFont;
            PluginInterface.UiBuilder.OnOpenConfigUi -= this.OnConfigCommandHandler;
            PluginInterface.Framework.OnUpdateEvent -= this.OnFrameworkUpdate;
            PluginInterface.UiBuilder.RebuildFonts();
            fpsHistoryInterval?.Stop();
            getBaseUIObj = null;
            getUI2ObjByName = null;
            toggleUIHook?.Disable();
            RemoveCommands();
            PluginInterface.Dispose();
        }

        public void Initialize(DalamudPluginInterface pluginInterface) {
            this.PluginInterface = pluginInterface;
            this.PluginConfig = (FPSPluginConfig) pluginInterface.GetPluginConfig() ?? new FPSPluginConfig();
            this.PluginConfig.Init(this, pluginInterface);
            fpsText = string.Empty;
            fpsHistory = new List<float>();

            var getBaseUIObjScan = PluginInterface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? 41 b8 01 00 00 00 48 8d 15 ?? ?? ?? ?? 48 8b 48 20 e8 ?? ?? ?? ?? 48 8b cf");
            var getUI2ObjByNameScan = PluginInterface.TargetModuleScanner.ScanText("e8 ?? ?? ?? ?? 48 8b cf 48 89 87 ?? ?? 00 00 e8 ?? ?? ?? ?? 41 b8 01 00 00 00");
            this.getBaseUIObj = Marshal.GetDelegateForFunctionPointer<GetBaseUIObjDelegate>(getBaseUIObjScan);
            this.getUI2ObjByName = Marshal.GetDelegateForFunctionPointer<GetUI2ObjByNameDelegate>(getUI2ObjByNameScan);


            var toggleUiPtr = pluginInterface.TargetModuleScanner.ScanText("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 0F B6 B9 ?? ?? ?? ?? B8 ?? ?? ?? ??");
            toggleUIHook = new Hook<ToggleUIDelegate>(toggleUiPtr, new ToggleUIDelegate(((ptr, b) => {
                gameUIHidden = (Marshal.ReadByte(ptr, 105168) & 4) == 0;
                return toggleUIHook.Original(ptr, b);
            })));

            toggleUIHook.Enable();

            fpsHistoryInterval = new Stopwatch();
            fpsHistoryInterval.Start();
            SetupCommands();
            PluginInterface.UiBuilder.OnBuildUi += this.BuildUI;
            PluginInterface.UiBuilder.OnOpenConfigUi += OnConfigCommandHandler;
            PluginInterface.Framework.OnUpdateEvent += OnFrameworkUpdate;
            PluginInterface.UiBuilder.OnBuildFonts += this.BuildFont;
        }

        private string FormatFpsValue(float value) {
            if (maxSeenFps > 1000) return PluginConfig.ShowDecimals ? $"{value,8:####0.00}" : $"{value,5:####0}";
            if (maxSeenFps > 100) return PluginConfig.ShowDecimals ? $"{value,7:###0.00}" : $"{value,4:###0}";
            return PluginConfig.ShowDecimals ? $"{value,6:##0.00}" : $"{value,3:##0}";
        }

        private void OnFrameworkUpdate(Framework framework) {
            try {
                if (!(fontBuilt || fontLoadFailed)) return;
                if (fpsHistoryInterval.ElapsedMilliseconds > 1000) {
                    fpsHistoryInterval.Restart();
                    // FPS values are only updated in memory once per second.
                    var fps = Marshal.PtrToStructure<float>(PluginInterface.Framework.Address.BaseAddress + 0x165C);
                    var windowInactive = Marshal.ReadByte(framework.Address.BaseAddress, 0x1668) == 1;
                    if (fps > maxSeenFps) maxSeenFps = fps;

                    fpsText = $"FPS:{FormatFpsValue(fps)}";
                    if (PluginConfig.ShowAverage || PluginConfig.ShowMinimum) {
                        if (!windowInactive) fpsHistory.Add(fps);
                        if (fpsHistory.Count > PluginConfig.HistorySnapshotCount) {
                            fpsHistory.RemoveRange(0, fpsHistory.Count - PluginConfig.HistorySnapshotCount);
                        }

                        if (PluginConfig.ShowAverage) {
                            fpsText += PluginConfig.MultiLine ? "\n" : " / ";
                            fpsText += $"Avg:{FormatFpsValue(fpsHistory.Average())}";
                        }

                        if (PluginConfig.ShowMinimum) {
                            fpsText += PluginConfig.MultiLine ? "\n" : " / ";
                            fpsText += $"Min:{FormatFpsValue(fpsHistory.Min())}";
                        }
                    }

                    windowSize = Vector2.Zero;
                }


                // https://github.com/karashiiro/PingPlugin
                if (this.PluginInterface.ClientState.LocalPlayer == null) {
                    chatHidden = false;
                    this.chatLogObject = IntPtr.Zero;
                    return;
                }

                if (chatLogObject == IntPtr.Zero) {
                    var baseUi = this.getBaseUIObj();
                    if (baseUi == IntPtr.Zero) return;
                    var baseOffset = Marshal.ReadIntPtr(baseUi, 32);
                    if (baseOffset == IntPtr.Zero) return;
                    this.chatLogObject = this.getUI2ObjByName(baseOffset, "ChatLog");
                    return;
                }       

                var chatLogProperties = Marshal.ReadIntPtr(this.chatLogObject, 0xC8);
                if (chatLogProperties == IntPtr.Zero) {
                    this.chatHidden = true;
                    return;
                }

                chatHidden = Marshal.ReadByte(chatLogProperties + 0x73) == 0;
            } catch (Exception ex) {
                PluginLog.LogError(ex.Message);
                chatHidden = false;
                this.chatLogObject = IntPtr.Zero;
            }
        }

        public void SetupCommands() {
            PluginInterface.CommandManager.AddHandler("/pfps", new Dalamud.Game.Command.CommandInfo(OnConfigCommandHandler) {
                HelpMessage = $"Open config window for {this.Name}. /pfps [show|hide|toggle|reset]",
                ShowInHelp = true
            });
        }


        public void OnConfigCommandHandler(object a, object b) {
            if (b is string s) {
                switch (s.ToLower()) {
                    case "toggle": {
                        PluginConfig.Enable = !PluginConfig.Enable;
                        break;
                    }
                    case "show": {
                        PluginConfig.Enable = true;
                        break;
                    }
                    case "hide": {
                        PluginConfig.Enable = false;
                        break;
                    }
                    case "reset": {
                        fpsHistory.Clear();
                        break;
                    }
                    default: {
                        drawConfigWindow = true;
                        break;
                    }
                }

                PluginConfig.Save();
            } else {
                drawConfigWindow = true;
            }
        }

        public void RemoveCommands() {
            PluginInterface.CommandManager.RemoveHandler("/pfps");
        }

        private void BuildFont() {
            var fontFile = Path.Combine(Path.GetDirectoryName(Location), "font.ttf");

            fontBuilt = false;
            if (File.Exists(fontFile)) {
                try {
                    font = ImGui.GetIO().Fonts.AddFontFromFileTTF(fontFile, PluginConfig.FontSize);
                    fontBuilt = true;
                } catch (Exception ex) {
                    PluginLog.Log($"Font failed to load. {fontFile}");
                    PluginLog.Log(ex.ToString());
                    fontLoadFailed = true;
                }
            } else {
                PluginLog.Log($"Font doesn't exist. {fontFile}");
                fontLoadFailed = true;
            }
        }

        internal void ReloadFont() {
            PluginInterface.UiBuilder.RebuildFonts();
        }
        
        private void BuildUI() {
            if (!fontBuilt && !fontLoadFailed) {
                PluginInterface.UiBuilder.RebuildFonts();
                return;
            }

            drawConfigWindow = drawConfigWindow && PluginConfig.DrawConfigUI();

            if (PluginConfig.FontChangeTime > 0) {
                if (DateTime.Now.Ticks - 10000000 > PluginConfig.FontChangeTime) {
                    PluginConfig.FontChangeTime = 0;
                    windowSize = Vector2.Zero;
                    ReloadFont();
                }
            }


            if ((gameUIHidden || chatHidden) && PluginConfig.HideInCutscene || !PluginConfig.Enable || string.IsNullOrEmpty(fpsText)) return;
            ImGui.SetNextWindowBgAlpha(PluginConfig.Alpha);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
            var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar;

            if (PluginConfig.Locked) {
                flags |= ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoMove;
            }

            if (fontBuilt) ImGui.PushFont(font);
            if (windowSize == Vector2.Zero) {
                windowSize = ImGui.CalcTextSize(fpsText) + (ImGui.GetStyle().WindowPadding * 2);
            }

            
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            ImGui.Begin("FPS##fpsPluginMonitorWindow", flags);
            ImGui.TextColored(PluginConfig.Colour, fpsText);
            ImGui.End();
            ImGui.PopStyleVar(6);
            if (fontBuilt) ImGui.PopFont();
        }
    }
}
