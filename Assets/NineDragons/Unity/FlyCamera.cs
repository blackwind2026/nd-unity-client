using UnityEngine;

namespace NineDragons.Client
{
    /// <summary>WASD + right-mouse-look fly camera (E/Q up/down, Shift = fast).</summary>
    public sealed class FlyCamera : MonoBehaviour
    {
        public float speed = 20f;
        public float fastMultiplier = 5f;
        public float lookSensitivity = 3f;

        private float _yaw, _pitch;

        private void Start()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x;
        }

        private void Update()
        {
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            var move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            if (Input.GetKey(KeyCode.E)) move.y += 1f;
            if (Input.GetKey(KeyCode.Q)) move.y -= 1f;
            float s = speed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);
            transform.position += transform.rotation * move * (s * Time.deltaTime);
        }
    }
}
