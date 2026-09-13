// 钓鱼场景一次性构建：命令行 -executeMethod FishingSceneBuilder.Build
// 或编辑器菜单 Tools → Build Fishing Scene
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FishingSceneBuilder
{
    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static Material MakeMat(string name, Color c)
    {
        string dir = "Assets/Materials";
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = $"{dir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Standard")) { color = c };
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    [MenuItem("Tools/Build Fishing Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "Fishing";

        // 水面
        var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
        water.name = "Water";
        water.transform.localScale = Vector3.one * 1.5f;   // 15m
        water.GetComponent<Renderer>().sharedMaterial =
            MakeMat("Water", new Color(0.1f, 0.3f, 0.45f));

        // 灯光
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // 相机：看得到渔轮（原点）和前方水面
        var camGo = new GameObject("Main Camera");
        var cam = camGo.AddComponent<Camera>();
        camGo.tag = "MainCamera";
        camGo.transform.position = new Vector3(0f, 1.0f, -1.0f);
        camGo.transform.LookAt(new Vector3(0f, 0f, 1.2f));
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.01f;

        // 渔轮模型挂 IMU 姿态节点
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/RollerHaptic.fbx");
        if (prefab == null) { Debug.LogError("FBX not found"); return; }
        var imuRig = new GameObject("IMU_Rig");
        imuRig.transform.position = new Vector3(0f, 0.12f, 0f);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        model.name = "RollerHapticModel";
        model.transform.SetParent(imuRig.transform, false);
        model.transform.localPosition = Vector3.zero;

        var rotor = FindDeep(model.transform, "RollerCAN_Rotor");
        if (rotor == null) { Debug.LogError("RollerCAN_Rotor node not found in FBX"); return; }

        // 摇柄手把（程序化，参数可调）
        var handle = rotor.gameObject.AddComponent<ReelHandle>();
        handle.Build();

        // 竿尖（鱼线起点，随 IMU 姿态）
        var rodTip = new GameObject("RodTip");
        rodTip.transform.SetParent(imuRig.transform, false);
        rodTip.transform.localPosition = new Vector3(0f, 0.10f, 0.08f);

        // 浮漂
        var bobber = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bobber.name = "Bobber";
        bobber.transform.localScale = Vector3.one * 0.03f;
        bobber.GetComponent<Renderer>().sharedMaterial = MakeMat("Bobber", Color.red);
        bobber.SetActive(false);

        // 鱼（胶囊体）
        var fish = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        fish.name = "Fish";
        fish.transform.localScale = new Vector3(0.08f, 0.15f, 0.08f);
        fish.GetComponent<Renderer>().sharedMaterial =
            MakeMat("Fish", new Color(0.5f, 0.6f, 0.7f));
        fish.SetActive(false);

        // 鱼线
        var lineGo = new GameObject("FishLine");
        var lr = lineGo.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.startWidth = lr.endWidth = 0.003f;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = Color.white;
        lineGo.SetActive(false);

        // 串口链路
        var serialGo = new GameObject("RollerHapticSerial");
        var serial = serialGo.AddComponent<RollerHapticSerial>();
        serial.coreS3Root = imuRig.transform;   // 整机（含手把）随 IMU 姿态
        serial.motorRoot = rotor;               // 转子/摇柄随编码器

        // 玩法
        var simGo = new GameObject("FishingSim");
        var sim = simGo.AddComponent<FishingSim>();
        sim.serial = serial;
        sim.rodTip = rodTip.transform;
        sim.bobber = bobber.transform;
        sim.fish = fish.transform;
        sim.fishLine = lr;
        sim.waterSurface = water.transform;

        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Fishing.unity");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        Debug.Log("FishingSceneBuilder: Fishing.unity created");
    }
}
