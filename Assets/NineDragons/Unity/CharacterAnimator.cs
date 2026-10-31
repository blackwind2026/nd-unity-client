using UnityEngine;

namespace NineDragons.Client
{
    /// <summary>
    /// Lightweight procedural motion for a body-part character until full skeletal XA playback
    /// is wired: a gentle breathing bob while idle, a faster bob + forward lean + slight sway
    /// while moving. Applied to the visual model child so it never fights the controller's
    /// authoritative root position.
    /// </summary>
    public sealed class CharacterAnimator : MonoBehaviour
    {
        public PlayerController Controller;   // optional: drives moving vs idle
        public bool ForceMoving;

        private Vector3 _baseLocalPos;
        private Quaternion _baseLocalRot;
        private float _phase;

        private void Start()
        {
            _baseLocalPos = transform.localPosition;
            _baseLocalRot = transform.localRotation;
        }

        private void Update()
        {
            bool moving = ForceMoving || (Controller != null && Controller.IsMoving);
            float dt = Time.deltaTime;
            _phase += dt * (moving ? 9f : 2.2f);

            float bob = Mathf.Sin(_phase) * (moving ? 0.06f : 0.02f);
            float sway = moving ? Mathf.Sin(_phase * 0.5f) * 3.0f : 0f;      // deg
            float lean = moving ? 6f : 0f;                                   // deg forward

            transform.localPosition = _baseLocalPos + new Vector3(0f, Mathf.Abs(bob), 0f);
            transform.localRotation = _baseLocalRot * Quaternion.Euler(lean, 0f, sway);
        }
    }
}
