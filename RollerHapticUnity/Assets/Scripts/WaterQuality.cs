/*
 * WaterQuality.cs — 水面 shader 编辑模式降质（挂在 Water 物体上）
 *
 * 非 Play 模式下 Game/Scene 视图每次交互都全屏重渲，GrabPass + fbm 噪声太贵。
 * 这里用 MaterialPropertyBlock（不改 .mat 资产、不弄脏场景）把 _Detail 调低：
 * _Detail=0 时 shader 只剩 4 层正弦解析法线，便宜一个数量级。
 * Play 中恢复 playModeDetail（默认 1 = 完整效果）。
 */
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(Renderer))]
public class WaterQuality : MonoBehaviour
{
    [Range(0f, 1f)] public float editModeDetail = 0.15f;
    [Range(0f, 1f)] public float playModeDetail = 1f;

    MaterialPropertyBlock mpb;
    float applied = -1f;

    void Apply()
    {
        float d = Application.isPlaying ? playModeDetail : editModeDetail;
        if (Mathf.Approximately(d, applied)) return;
        applied = d;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        var r = GetComponent<Renderer>();
        r.GetPropertyBlock(mpb);
        mpb.SetFloat("_Detail", d);
        r.SetPropertyBlock(mpb);
    }

    void OnEnable() { applied = -1f; Apply(); }
    void Update() { Apply(); }
}
