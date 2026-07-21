using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using effetopo.Commands;

namespace effetopo.Services
{
    /// <summary>
    /// Builds the Toposolid Tools split button and keeps the most recently used command as the default.
    /// </summary>
    public class ToposolidToolsRibbonService
    {
        private const string Icon16 = "pack://application:,,,/effetopo;component/Resources/Icons/RibbonIcon16.png";
        private const string Icon32 = "pack://application:,,,/effetopo;component/Resources/Icons/RibbonIcon32.png";

        private static readonly Dictionary<string, Type> CommandTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [JoinMultipleToposolidsCommand.COMMAND_NAME] = typeof(JoinMultipleToposolidsCommand),
            [MergeProposalToposolidCommand.COMMAND_NAME] = typeof(MergeProposalToposolidCommand),
            [FloorFollowTopoCommand.COMMAND_NAME] = typeof(FloorFollowTopoCommand),
            [ModifyTopoCommand.COMMAND_NAME] = typeof(ModifyTopoCommand),
            [SetElevationCommand.COMMAND_NAME] = typeof(SetElevationCommand),
        };

        private static ToposolidToolsRibbonService _instance;
        private static readonly object _lock = new object();

        private SplitButton _splitButton;
        private readonly Dictionary<string, PushButton> _pushButtons = new Dictionary<string, PushButton>(StringComparer.Ordinal);
        private readonly Uri _icon16Uri = new Uri(Icon16, UriKind.Absolute);
        private readonly Uri _icon32Uri = new Uri(Icon32, UriKind.Absolute);
        private readonly string _assemblyPath = Assembly.GetExecutingAssembly().Location;

        public static ToposolidToolsRibbonService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ToposolidToolsRibbonService();
                    }
                }
                return _instance;
            }
        }

        private ToposolidToolsRibbonService() { }

        public void CreateSplitButton(RibbonPanel panel)
        {
            var definitions = GetCommandDefinitions();
            string lastUsed = ToposolidToolsSettingsService.Instance.Load().LastUsedCommandId;
            var orderedIds = GetOrderedCommandIds(lastUsed, definitions.Select(d => d.CommandId));

            var splitData = new SplitButtonData("ToposolidCommands", "Toposolid\nTools");
            _splitButton = panel.AddItem(splitData) as SplitButton;
            if (_splitButton == null) return;

            _splitButton.ToolTip = "Toposolid merge and join tools";
            _splitButton.IsSynchronizedWithCurrentItem = true;
            _pushButtons.Clear();

            bool separatorPendingBeforeFloor = false;
            foreach (string commandId in orderedIds)
            {
                var definition = definitions.First(d => d.CommandId == commandId);

                if (definition.IsFloorCommand && separatorPendingBeforeFloor)
                {
                    _splitButton.AddSeparator();
                    separatorPendingBeforeFloor = false;
                }

                PushButton pushButton = AddPushButton(definition);
                if (pushButton != null)
                    _pushButtons[commandId] = pushButton;

                if (!definition.IsFloorCommand)
                    separatorPendingBeforeFloor = true;
            }

            string defaultId = orderedIds.FirstOrDefault();
            if (!string.IsNullOrEmpty(defaultId) && _pushButtons.TryGetValue(defaultId, out PushButton defaultButton))
            {
                try
                {
                    _splitButton.CurrentButton = defaultButton;
                    Log.Debug("Toposolid Tools default command set to {CommandId}", defaultId);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not set default Toposolid tool to {CommandId}", defaultId);
                }
            }
        }

        public void RecordCommandUsed(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId) || !_pushButtons.ContainsKey(commandId))
                return;

            ToposolidToolsSettingsService.Instance.SaveLastUsedCommand(commandId);

            if (_splitButton == null) return;
            try
            {
                _splitButton.CurrentButton = _pushButtons[commandId];
            }
            catch (Exception ex)
            {
                Log.Debug("Could not update Toposolid Tools default button: {Error}", ex.Message);
            }
        }

        public bool IsToposolidToolCommand(string commandId)
        {
            return _pushButtons.ContainsKey(commandId);
        }

        private static IEnumerable<string> GetOrderedCommandIds(string lastUsed, IEnumerable<string> allCommandIds)
        {
            var all = allCommandIds.ToList();
            var ordered = new List<string>();

            if (!string.IsNullOrWhiteSpace(lastUsed) && all.Contains(lastUsed))
                ordered.Add(lastUsed);

            foreach (string id in all)
            {
                if (!ordered.Contains(id))
                    ordered.Add(id);
            }

            return ordered;
        }

        private PushButton AddPushButton(ToposolidToolDefinition definition)
        {
            if (!CommandTypes.TryGetValue(definition.CommandId, out Type commandType))
            {
                return null;
            }

            var data = new PushButtonData(
                definition.CommandId,
                definition.ItemText,
                _assemblyPath,
                commandType.FullName!)
            {
                Image = new BitmapImage(_icon16Uri),
                LargeImage = new BitmapImage(_icon32Uri),
                ToolTip = definition.ToolTip
            };

            return _splitButton.AddPushButton(data);
        }

        private static List<ToposolidToolDefinition> GetCommandDefinitions()
        {
            return new List<ToposolidToolDefinition>
            {
                new ToposolidToolDefinition(
                    JoinMultipleToposolidsCommand.COMMAND_NAME,
                    "Join Multiple\nToposolids",
                    "Join Multiple Toposolids\nMerge 2+ Toposolids using max elevation priority",
                    isFloorCommand: false),
                new ToposolidToolDefinition(
                    MergeProposalToposolidCommand.COMMAND_NAME,
                    "Merge Proposal\nToposolid",
                    "Merge Proposal Toposolid\nMerge Proposal into Existing Toposolid (Proposal priority)",
                    isFloorCommand: false),
                new ToposolidToolDefinition(
                    FloorFollowTopoCommand.COMMAND_NAME,
                    "Floor Follow\nTopo",
                    "Floor Follow Toposolid\nMake Floor follow Toposolid surface elevation",
                    isFloorCommand: true),
                new ToposolidToolDefinition(
                    ModifyTopoCommand.COMMAND_NAME,
                    "Modify\nTopo",
                    "Modify Toposolid\nSculpt surface: inflate, mesh control, shape by point, smooth",
                    isFloorCommand: false),
                new ToposolidToolDefinition(
                    SetElevationCommand.COMMAND_NAME,
                    "Set\nElevation",
                    "Set Elevation\nAssign elevations to model lines and splines with optional labels",
                    isFloorCommand: true)
            };
        }

        private sealed class ToposolidToolDefinition
        {
            public ToposolidToolDefinition(string commandId, string itemText, string toolTip, bool isFloorCommand)
            {
                CommandId = commandId;
                ItemText = itemText;
                ToolTip = toolTip;
                IsFloorCommand = isFloorCommand;
            }

            public string CommandId { get; }
            public string ItemText { get; }
            public string ToolTip { get; }
            public bool IsFloorCommand { get; }
        }
    }
}
