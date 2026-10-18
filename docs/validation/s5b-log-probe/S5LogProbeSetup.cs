using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// VRC Remote Test spike S5b: puts S5LogForgeProbe into the world scene (creating its
/// U# program asset first) and takes it out again after the spike.
/// </summary>
public static class S5LogProbeSetup
{
    private const string ScenePath = "Assets/Scenes/VRCDefaultWorldScene.unity";
    private const string ScriptPath = "Assets/S5LogProbe/S5LogForgeProbe.cs";
    private const string ProgramAssetPath = "Assets/S5LogProbe/S5LogForgeProbe.asset";
    private const string ProbeObjectName = "S5LogProbe";

    [MenuItem("Tools/VRC Remote Test S5/Add Log Probe")]
    public static void AddLogProbe()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError($"[S5] {ScriptPath} was not found.");
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) == null)
        {
            var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            programAsset.sourceCsScript = script;
            AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
            AssetDatabase.Refresh();
        }

        UdonSharpProgramAsset.CompileAllCsPrograms(forceCompile: true);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var probe = GameObject.Find(ProbeObjectName) ?? new GameObject(ProbeObjectName);
        if (probe.GetComponent<S5LogForgeProbe>() == null)
        {
            UdonSharpUndo.AddComponent<S5LogForgeProbe>(probe);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[S5] {ProbeObjectName} is in {ScenePath}. Run Remote Build & Test to deploy it.");
    }

    [MenuItem("Tools/VRC Remote Test S5/Remove Log Probe")]
    public static void RemoveLogProbe()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var probe = GameObject.Find(ProbeObjectName);
        if (probe != null)
        {
            Object.DestroyImmediate(probe);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        Debug.Log($"[S5] {ProbeObjectName} removed from {ScenePath}. Delete Assets/S5LogProbe to remove the scripts.");
    }
}
