/*
 * ReelHandle.cs — 渔轮摇柄手把（程序化生成，挂在 RollerCAN_Rotor 下）
 *
 * 转子绕本地 Y 轴旋转（RollerHapticSerial 驱动），手把作为其子物体随转。
 * 结构：摇臂（细长 Cube，沿 armAxis 径向伸出）+ 握把（Cylinder，沿 gripAxis
 * 立在摇臂末端）。参数全部 Inspector 可调；改参数后右键组件 → Rebuild。
 * 美术化 Blender 重做后置。
 */
using UnityEngine;

public class ReelHandle : MonoBehaviour
{
    [Header("摇臂")]
    public float armLength = 0.05f;       // 摇臂半径（从轴心到握把）
    public float armThickness = 0.006f;
    public float armOffsetY = 0.012f;     // 摇臂离转子端面的高度
    [Header("握把")]
    public float gripRadius = 0.006f;
    public float gripLength = 0.025f;
    [Header("轴向（默认转子绕 Y 转）")]
    public Vector3 armAxis = Vector3.right;   // 摇臂伸出方向
    public Vector3 gripAxis = Vector3.up;     // 握把指向（与转轴平行）

    [ContextMenu("Rebuild")]
    public void Build()
    {
        // 清掉旧的
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (c.name == "CrankArm" || c.name == "CrankGrip")
            {
                if (Application.isPlaying) Destroy(c.gameObject);
                else DestroyImmediate(c.gameObject);
            }
        }

        Vector3 arm = armAxis.normalized;
        Vector3 grip = gripAxis.normalized;

        var armGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        armGo.name = "CrankArm";
        var at = armGo.transform;
        at.SetParent(transform, false);
        at.localPosition = arm * (armLength * 0.5f) + grip * armOffsetY;
        at.localRotation = Quaternion.FromToRotation(Vector3.right, arm);
        at.localScale = new Vector3(armLength, armThickness, armThickness);
        if (Application.isPlaying) Destroy(armGo.GetComponent<Collider>());

        var gripGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        gripGo.name = "CrankGrip";
        var gt = gripGo.transform;
        gt.SetParent(transform, false);
        gt.localPosition = arm * armLength + grip * (armOffsetY + gripLength * 0.5f);
        gt.localRotation = Quaternion.FromToRotation(Vector3.up, grip);
        // Cylinder 原型高 2、半径 0.5
        gt.localScale = new Vector3(gripRadius * 2f, gripLength * 0.5f, gripRadius * 2f);
        if (Application.isPlaying) Destroy(gripGo.GetComponent<Collider>());
    }

    void Awake()
    {
        if (transform.Find("CrankArm") == null) Build();
    }
}
