using UnityEngine;

// Play 中相机朝向目标：位置不动，只改 rotation。
// 瞄准点 = target.position + aimOffset（世界空间固定偏移，不随目标旋转——
// 避免相机随 IMU 姿态"自转"）；dampSpeed 为指数平滑阻尼（1/s），0 = 关闭平滑硬跟随。
public class CameraAim : MonoBehaviour
{
    public Transform target;                 // 挂 RollerHapticModel
    public Vector3 aimOffset = new Vector3(0f, -0.3f, 0.4f);   // 世界空间，往水面方向偏保浮漂视野
    [Tooltip("追踪速度（1/s），越大跟得越紧；0 = 关闭平滑")]
    public float dampSpeed = 4f;

    Quaternion smRot;
    bool initialized;

    void LateUpdate()
    {
        if (target == null) return;
        Vector3 aimPoint = target.position + aimOffset;
        Quaternion want = Quaternion.LookRotation(aimPoint - transform.position, Vector3.up);
        if (!initialized || dampSpeed <= 0f)
        {
            smRot = want;
            initialized = true;
        }
        else
        {
            smRot = Quaternion.Slerp(smRot, want, 1f - Mathf.Exp(-dampSpeed * Time.deltaTime));
        }
        transform.rotation = smRot;
    }
}
