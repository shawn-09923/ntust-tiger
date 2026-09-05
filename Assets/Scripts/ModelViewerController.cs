using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// Turntable viewer for a single showcased model.
/// Drag (mouse or one finger) spins the model on its Y axis and tilts the camera up/down,
/// and a flick keeps it spinning. Mouse wheel or a two-finger pinch dollies the camera in and out.
/// </summary>
// Runs after the EventSystem so a press can already see this frame's UI raycast.
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Camera))]
public class ModelViewerController : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Root transform of the model to showcase.")]
    [SerializeField] private Transform modelRoot;

    [Tooltip("Extra offset applied to the auto-computed pivot (bounds center of the model).")]
    [SerializeField] private Vector3 pivotOffset = Vector3.zero;

    [Header("Rotation")]
    [Tooltip("Degrees of model spin per pixel of horizontal drag.")]
    [SerializeField] private float yawSpeed = 0.28f;

    [Tooltip("Degrees of camera tilt per pixel of vertical drag.")]
    [SerializeField] private float pitchSpeed = 0.18f;

    [SerializeField] private float minPitch = -25f;
    [SerializeField] private float maxPitch = 70f;

    [Header("Inertia")]
    [Tooltip("How quickly a flick spins down; higher stops sooner, 0 spins on forever.")]
    [SerializeField] private float inertiaDamping = 3.5f;

    [Tooltip("Degrees per second below which a flick is treated as stopped.")]
    [SerializeField] private float inertiaCutoff = 3f;

    [Header("Zoom")]
    [SerializeField] private float minDistance = 1.2f;
    [SerializeField] private float maxDistance = 6f;

    [Tooltip("Zoom exponent per wheel notch; 0.12 moves roughly 11% closer.")]
    [SerializeField] private float scrollZoomSpeed = 0.12f;

    [Tooltip("Zoom exponent per pixel of pinch; ~400 px spans the whole distance range.")]
    [SerializeField] private float pinchZoomSpeed = 0.004f;

    [Header("Feel")]
    [Tooltip("Higher is snappier; 0 disables smoothing.")]
    [SerializeField] private float smoothing = 14f;

    [Tooltip("Ignore drags that start on top of a UI element.")]
    [SerializeField] private bool blockWhenOverUI = true;

    // Pivot the camera orbits and the model spins around, in world space.
    private Vector3 _pivot;

    // Pose the model had on Start; all spin is applied relative to it.
    private Quaternion _baseRotation;
    private Vector3 _basePosition;

    // Fixed horizontal angle of the camera, taken from the scene's framing.
    private float _azimuth;

    // The framing the scene was authored with, restored by ResetView.
    private float _initialPitch;
    private float _initialDistance;

    // Desired values, driven by input.
    private float _yaw;
    private float _pitch;
    private float _distance;

    // Displayed values, eased towards the desired ones.
    private float _shownYaw;
    private float _shownPitch;
    private float _shownDistance;

    // Degrees per second carried over from the last drag.
    private float _yawVelocity;
    private float _pitchVelocity;

    private bool _touchDragging;
    private bool _mouseDragging;
    private float _lastPinchDistance;

    private bool IsDragging => _touchDragging || _mouseDragging;

    private void Awake()
    {
        if (modelRoot == null)
        {
            Debug.LogError($"{nameof(ModelViewerController)}: no model root assigned.", this);
            enabled = false;
            return;
        }

        _pivot = ResolvePivot();
        _baseRotation = modelRoot.rotation;
        _basePosition = modelRoot.position;

        // Derive the starting orbit from however the camera was placed in the scene.
        Vector3 offset = transform.position - _pivot;
        _distance = Mathf.Clamp(offset.magnitude, minDistance, maxDistance);
        _azimuth = Mathf.Atan2(-offset.x, -offset.z) * Mathf.Rad2Deg;
        _pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.y / Mathf.Max(offset.magnitude, 0.0001f), -1f, 1f)) * Mathf.Rad2Deg,
                             minPitch, maxPitch);

        _initialPitch = _pitch;
        _initialDistance = _distance;

        _shownYaw = _yaw;
        _shownPitch = _pitch;
        _shownDistance = _distance;
        ApplyPose();
    }

    private void OnEnable()
    {
        EnhancedTouchSupport.Enable();
    }

    private void OnDisable()
    {
        EnhancedTouchSupport.Disable();
        _touchDragging = false;
        _mouseDragging = false;
        _lastPinchDistance = 0f;
        StopSpin();
    }

    /// <summary>Eases the view back to the framing the scene was authored with. Hook this up to a UI button.</summary>
    public void ResetView()
    {
        StopSpin();
        _yaw = 0f;
        _pitch = _initialPitch;
        _distance = _initialDistance;
    }

    private void Update()
    {
        if (ReadTouchInput())
        {
            // A touchscreen owns the frame; drop any mouse drag the browser may also be reporting.
            _mouseDragging = false;
        }
        else
        {
            ReadMouseInput();
        }

        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        if (!IsDragging)
        {
            ApplyInertia(dt);
        }

        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        _distance = Mathf.Clamp(_distance, minDistance, maxDistance);

        float t = smoothing > 0f ? 1f - Mathf.Exp(-smoothing * dt) : 1f;
        _shownYaw = Mathf.Lerp(_shownYaw, _yaw, t);
        _shownPitch = Mathf.Lerp(_shownPitch, _pitch, t);
        _shownDistance = Mathf.Lerp(_shownDistance, _distance, t);

        ApplyPose();
    }

    /// <summary>Handles touch; returns true when a touchscreen is driving the view this frame.</summary>
    private bool ReadTouchInput()
    {
        if (!EnhancedTouchSupport.enabled)
        {
            return false;
        }

        var touches = ETouch.activeTouches;
        if (touches.Count == 0)
        {
            _touchDragging = false;
            _lastPinchDistance = 0f;

            // A press the enhanced-touch layer has not surfaced yet still belongs to the touchscreen.
            return Touchscreen.current != null && Touchscreen.current.press.isPressed;
        }

        if (touches.Count >= 2)
        {
            // Two fingers: pinch to dolly, and never rotate at the same time.
            if (_touchDragging)
            {
                _touchDragging = false;
                StopSpin();
            }

            float pinch = Vector2.Distance(touches[0].screenPosition, touches[1].screenPosition);
            if (_lastPinchDistance > 0f)
            {
                Zoom((pinch - _lastPinchDistance) * pinchZoomSpeed);
            }
            _lastPinchDistance = pinch;
            return true;
        }

        _lastPinchDistance = 0f;
        var touch = touches[0];

        if (touch.began)
        {
            _touchDragging = !(blockWhenOverUI && IsPointerOverUI(touch.touchId));
            StopSpin();
        }

        if (_touchDragging)
        {
            Rotate(touch.delta);
        }

        return true;
    }

    private void ReadMouseInput()
    {
        var mouse = Mouse.current;
        if (mouse == null)
        {
            return;
        }

        if (mouse.leftButton.wasPressedThisFrame)
        {
            _mouseDragging = !(blockWhenOverUI && IsPointerOverUI());
            StopSpin();
        }
        else if (!mouse.leftButton.isPressed)
        {
            _mouseDragging = false;
        }

        if (_mouseDragging)
        {
            Rotate(mouse.delta.ReadValue());
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (!Mathf.Approximately(scroll, 0f))
        {
            // Desktop reports 120 per notch; browsers and trackpads report small values.
            float notches = Mathf.Abs(scroll) > 1f ? scroll / 120f : scroll;
            Zoom(notches * scrollZoomSpeed);
        }
    }

    /// <summary>Drag right spins the model's front to the right; drag down tips its top towards the viewer.</summary>
    private void Rotate(Vector2 dragDelta)
    {
        float yawDelta = -dragDelta.x * yawSpeed;
        float pitchDelta = -dragDelta.y * pitchSpeed;

        _yaw += yawDelta;
        _pitch += pitchDelta;

        // Blend in the instantaneous rate, so a flick carries the speed of its last few frames
        // while letting go after holding still launches nothing.
        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        _yawVelocity = Mathf.Lerp(_yawVelocity, yawDelta / dt, 0.5f);
        _pitchVelocity = Mathf.Lerp(_pitchVelocity, pitchDelta / dt, 0.5f);
    }

    private void ApplyInertia(float dt)
    {
        if (Mathf.Abs(_yawVelocity) < inertiaCutoff && Mathf.Abs(_pitchVelocity) < inertiaCutoff)
        {
            StopSpin();
            return;
        }

        _yaw += _yawVelocity * dt;
        _pitch += _pitchVelocity * dt;

        // Running into a pitch limit should absorb the flick rather than have it hang there.
        if (_pitch <= minPitch || _pitch >= maxPitch)
        {
            _pitchVelocity = 0f;
        }

        float decay = Mathf.Exp(-inertiaDamping * dt);
        _yawVelocity *= decay;
        _pitchVelocity *= decay;
    }

    private void StopSpin()
    {
        _yawVelocity = 0f;
        _pitchVelocity = 0f;
    }

    /// <summary>Positive amount moves the camera closer. Exponential, so zooming in and back out is symmetric.</summary>
    private void Zoom(float amount)
    {
        _distance = Mathf.Clamp(_distance * Mathf.Exp(-amount), minDistance, maxDistance);
    }

    private void ApplyPose()
    {
        // Spin the model around the pivot's vertical axis, keeping its authored pose as the base.
        Quaternion spin = Quaternion.AngleAxis(_shownYaw, Vector3.up);
        modelRoot.SetPositionAndRotation(_pivot + spin * (_basePosition - _pivot), spin * _baseRotation);

        // Orbit the camera vertically and dolly it along the view direction.
        Quaternion orbit = Quaternion.Euler(_shownPitch, _azimuth, 0f);
        Vector3 position = _pivot + orbit * new Vector3(0f, 0f, -_shownDistance);
        transform.SetPositionAndRotation(position, Quaternion.LookRotation(_pivot - position, Vector3.up));
    }

    private Vector3 ResolvePivot()
    {
        var renderers = modelRoot.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return modelRoot.position + pivotOffset;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        return bounds.center + pivotOffset;
    }

    // The new input module keys pointers by device, so the mouse has to use the parameterless overload.
    private static bool IsPointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private static bool IsPointerOverUI(int touchId)
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touchId);
    }
}
