// 一次性场景构建脚本：命令行 -executeMethod SceneBuilder.Build
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SceneBuilder
{
    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "Main";

        // 地面
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = Vector3.one * 2f;

        // 灯光
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // 相机
        var camGo = new GameObject("Main Camera");
        var cam = camGo.AddComponent<Camera>();
        camGo.tag = "MainCamera";
        camGo.transform.position = new Vector3(0.13f, 0.10f, -0.13f);
        camGo.transform.LookAt(new Vector3(0f, 0.03f, 0f));
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.01f;

        // 模型
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/RollerHaptic.fbx");
        if (prefab == null) { Debug.LogError("FBX not found"); return; }
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        model.name = "RollerHapticModel";
        model.transform.position = new Vector3(0f, 0.001f, 0f);

        // 绑定同步脚本
        var syncGo = new GameObject("RollerHapticSync");
        var sync = syncGo.AddComponent<RollerHapticSync>();
        var staticGroup = FindDeep(model.transform, "Static_Group");
        var rotor = FindDeep(model.transform, "RollerCAN_Rotor");
        sync.coreS3Root = staticGroup != null ? staticGroup : model.transform;
        sync.motorRoot = rotor;
        if (rotor == null) Debug.LogError("RollerCAN_Rotor node not found in FBX");
        else Debug.Log("SceneBuilder: bound rotor = " + rotor.name);

        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Main.unity");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        Debug.Log("SceneBuilder: Main.unity created");
    }
}
