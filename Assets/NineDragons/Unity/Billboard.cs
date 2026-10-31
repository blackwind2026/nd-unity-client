using UnityEngine;

namespace NineDragons.Client
{
    /// <summary>Keeps a label/sprite facing the camera (name tags, HP bars).</summary>
    public sealed class Billboard : MonoBehaviour
    {
        private Camera _cam;
        private void LateUpdate()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            // face the same way the camera looks so text reads correctly (not mirrored)
            transform.rotation = _cam.transform.rotation;
        }
    }
}
