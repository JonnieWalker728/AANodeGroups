extern alias Il2Cpp;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Builds native project-data fixtures for opt-in integration verification only.
/// No editor nodes, resources, history entries, or production projects are modified here.
/// </summary>
internal static class NativeProjectFixture
{
    /// <summary>
    /// Creates a fresh connected Entry → Script → Selection → Exit graph. Its two
    /// choice options intentionally share the exit target while retaining distinct
    /// OptionIds and one default option, exposing accidental branch deduplication.
    /// </summary>
    /// <returns>Native data suitable for the application's normal project-load path.</returns>
    /// <remarks>
    /// Run on the Unity main thread after IL2CPP initialization. Except for the application's
    /// reserved all-zero entry identity, IDs are generated once per returned fixture and
    /// remain stable for its subsequent load/save/compile tests.
    /// Native constructors retain application defaults and the current format version.
    /// Execution and compilation still require runtime verification: interop metadata
    /// cannot prove graph-loader sequencing or optional auxiliary-script semantics.
    /// </remarks>
    internal static ProjectData Build()
    {
        var project = ProjectData.GetEmpty();
        project.nodes ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<NodeData>();
        project.nodes.Clear();
        project.ProjectId = Guid.NewGuid().ToString("D");
        project.ProjectName = "NodeGroups connected integration fixture";
        project.PreviewHeader = "NodeGroups";
        project.PreviewTitle = "保留剧情图与分支身份";

        var entry = new EntryNodeData { Title = "NodeGroups 入口", Header = "运行时验证" };
        var script = new ScriptNodeData { NodeName = "非空台词节点" };
        var selection = new SelectionNodeData();
        var exit = new ExitNodeData { IsEnding = true, EndText = "NodeGroups 测试结束" };
        InitializeNode(entry, -720, 0);
        // AzureArchive reserves Guid.Empty for its unique entry node. A randomly assigned
        // entry ID is replaced by the editor, breaking the captured graph's entry identity.
        entry.Guid = NativeGuid.Parse(Guid.Empty.ToString("D"));
        InitializeNode(script, -300, -140);
        InitializeNode(selection, 160, -140);
        InitializeNode(exit, 620, -140);

        script.Scripts ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<ScriptData>();
        script.Scripts.Clear();
        // ScriptData's parameterless constructor is a serialization container, not the
        // editor's new-line initializer. Derive the data from native Script defaults so
        // MatchWithPrev receives the application's complete stage-slot structure.
        var line = new ScriptData(new Script(isDialogScript: true, isEmpty: false))
        {
            LineId = Guid.NewGuid().ToString("D"),
            text = "NodeGroups 原剧情保护测试：两条选项保留各自的身份。"
        };
        EnsureScriptCollections(line);
        script.Scripts.Add(line);

        selection.Options ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<ChoiceOptionData>();
        selection.Options.Clear();
        selection.UnusedSelectionTexts ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<UnusedSelectionTextData>();
        selection.UnusedSelectionTexts.Clear();
        selection.selectionTexts ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<string>();
        selection.selectionTexts.Clear(); // Current Options are authoritative, not legacy text migration.
        var firstOption = new ChoiceOptionData
        {
            OptionId = Guid.NewGuid().ToString("D"),
            Text = "选项 A（默认）",
            TargetNodeId = new Il2Cpp::Il2CppSystem.Nullable<NativeGuid>(exit.Guid)
        };
        var secondOption = new ChoiceOptionData
        {
            OptionId = Guid.NewGuid().ToString("D"),
            Text = "选项 B（相同目标）",
            TargetNodeId = new Il2Cpp::Il2CppSystem.Nullable<NativeGuid>(exit.Guid)
        };
        selection.Options.Add(firstOption);
        selection.Options.Add(secondOption);
        selection.DefaultOptionId = firstOption.OptionId;
        selection.defaultSelectionIndex = 0;

        entry.ConnectionsTo.Add(script.Guid);
        script.ConnectionsTo.Add(selection.Guid);
        // There is one visual node connection but two independently identified choice
        // options. Never invent duplicate NodeData targets to stand in for OptionIds.
        selection.ConnectionsTo.Add(exit.Guid);
        exit.EnsureAuxiliaryScript();
        if (exit.NeScriptDirty != null) EnsureScriptCollections(exit.NeScriptDirty);

        project.nodes.Add(entry);
        project.nodes.Add(script);
        project.nodes.Add(selection);
        project.nodes.Add(exit);
        return project;
    }

    /// <summary>
    /// Creates an independent Entry → Exit project with no ScriptNode. It exercises
    /// first-session palette font fallback and permits a sole, unconnected temporary
    /// ScriptNode to be created and destroyed through its original creation command.
    /// </summary>
    /// <remarks>Only the opt-in report process saves or loads this data; production projects are never used.</remarks>
    internal static ProjectData BuildMinimal()
    {
        var project = ProjectData.GetEmpty();
        project.nodes ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<NodeData>();
        project.nodes.Clear();
        project.ProjectId = Guid.NewGuid().ToString("D");
        project.ProjectName = "NodeGroups isolated style lifetime fixture";
        project.PreviewHeader = "NodeGroups";
        project.PreviewTitle = "原生字体与视觉生命周期验证";
        var entry = new EntryNodeData { Title = "样式验证入口", Header = "隔离测试" };
        var exit = new ExitNodeData { IsEnding = true, EndText = "样式验证结束" };
        InitializeNode(entry, -400, 140);
        entry.Guid = NativeGuid.Parse(Guid.Empty.ToString("D"));
        InitializeNode(exit, 400, -200);
        entry.ConnectionsTo.Add(exit.Guid);
        exit.EnsureAuxiliaryScript();
        if (exit.NeScriptDirty != null) EnsureScriptCollections(exit.NeScriptDirty);
        project.nodes.Add(entry);
        project.nodes.Add(exit);
        return project;
    }

    /// <summary>Initializes real native node identity, coordinates, and its original outgoing-target list.</summary>
    private static void InitializeNode(NodeData node, float x, float y)
    {
        node.Guid = NativeGuid.Parse(Guid.NewGuid().ToString("D"));
        node.X = x;
        node.Y = y;
        node.ConnectionsTo ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<NativeGuid>();
        node.ConnectionsTo.Clear();
    }

    /// <summary>Ensures empty native list values without replacing defaults for audio, background, speaker, or transitions.</summary>
    private static void EnsureScriptCollections(ScriptData script)
    {
        script.characters ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<ScriptData.CharacterRecordData>();
        script.highlightedSlotNums ??= new Il2Cpp::Il2CppSystem.Collections.Generic.List<int>();
    }
}
