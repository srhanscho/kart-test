using UnityEngine;

/// <summary>Smooth third-person chase camera.</summary>
public class FollowCamera : MonoBehaviour
{
    [SerializeField] Transform target;
    [SerializeField] float distance = 8f;
    [SerializeField] float height = 3.2f;
    [SerializeField] float lookHeight = 1.2f;
    [SerializeField] float positionSmoothTime = 0.12f;
    [SerializeField] float yawSharpness = 4f;
    [SerializeField] float rotationSharpness = 12f;

    [Tooltip("Extra field of view while the target is boosting.")]
    [SerializeField] float boostFovKick = 12f;

    Vector3 velocity;
    float yaw;
    Camera cam;
    KartController kart;
    float baseFov = -1f;

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
        kart = target != null ? target.GetComponent<KartController>() : null;
        Snap();
    }

    void Start() => Snap();

    void Snap()
    {
        if (target == null) return;
        yaw = target.eulerAngles.y;
        transform.position = DesiredPosition();
        transform.rotation = DesiredRotation();
        velocity = Vector3.zero;
    }

    void LateUpdate()
    {
        if (target == null) return;
        float dt = Time.deltaTime;
        yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, 1f - Mathf.Exp(-yawSharpness * dt));
        transform.position = Vector3.SmoothDamp(transform.position, DesiredPosition(), ref velocity, positionSmoothTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, DesiredRotation(), 1f - Mathf.Exp(-rotationSharpness * dt));

        // FOV kick while boosting (turbo item or mini-turbo).
        if (cam == null) cam = GetComponent<Camera>();
        if (kart == null) kart = target.GetComponent<KartController>();
        if (cam != null)
        {
            if (baseFov < 0f) baseFov = cam.fieldOfView;
            float wanted = baseFov + (kart != null && kart.IsBoosting ? boostFovKick : 0f);
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, wanted, 1f - Mathf.Exp(-6f * dt));
        }
    }

    Vector3 DesiredPosition() =>
        target.position + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, height, -distance);

    Quaternion DesiredRotation()
    {
        Vector3 look = target.position + Vector3.up * lookHeight - transform.position;
        return look.sqrMagnitude > 0.001f ? Quaternion.LookRotation(look) : transform.rotation;
    }
}
