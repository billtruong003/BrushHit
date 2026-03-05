using UnityEngine;

namespace CleanRender
{
    /// <summary>
    /// Attach to the player (or any character) to interact with grass.
    /// Automatically registers/unregisters with GrassInstanceRenderer.
    /// 
    /// Features:
    ///   - Auto-register with grass system on enable
    ///   - Configurable bend radius and strength
    ///   - Speed-based radius scaling (running = wider bend)
    ///   - Optional dust/particle effect on movement
    ///   - Works with ToonGrass shader's _InteractorPositions array
    /// 
    /// Usage:
    ///   1. Add this component to your player GameObject
    ///   2. That's it — grass will bend when you walk through it
    ///   
    /// For NPCs/animals: add this component to them too, each gets a slot
    /// (up to 8 interactors supported simultaneously by the grass shader)
    /// </summary>
    [AddComponentMenu("CleanRender/Player Foliage Interactor")]
    public class PlayerFoliageInteractor : MonoBehaviour
    {
        [Header("━━━ Interaction Settings ━━━")]
        [Tooltip("Base radius of grass bending around this character")]
        [Range(0.5f, 5f)]
        public float bendRadius = 1.5f;

        [Tooltip("How strongly grass bends away")]
        [Range(0.1f, 3f)]
        public float bendStrength = 1.0f;

        [Tooltip("Scale radius based on movement speed")]
        public bool speedScaling = true;

        [Tooltip("Radius multiplier at max speed")]
        [Range(1f, 3f)]
        public float maxSpeedRadiusMultiplier = 1.5f;

        [Tooltip("Speed considered 'max' for radius scaling")]
        public float maxSpeed = 10f;

        [Header("━━━ Ground Detection ━━━")]
        [Tooltip("Offset interaction point downward (for characters with pivot at center)")]
        public float groundOffset = 0f;

        [Tooltip("Use a specific child transform as the interaction point (e.g., feet)")]
        public Transform overridePoint;

        [Header("━━━ Effects ━━━")]
        [Tooltip("Optional particle system to play when moving through grass")]
        public ParticleSystem grassParticles;

        [Tooltip("Minimum speed to trigger particles")]
        public float particleSpeedThreshold = 1f;

        // ── Internal ──
        private Vector3 _lastPosition;
        private float _currentSpeed;
        private float _currentRadius;
        private bool _isInGrass;
        private float _particleCooldown;

        // ── Static: direct material updates for performance ──
        private static readonly int _InteractorPositionsID = Shader.PropertyToID("_InteractorPositions");
        private static readonly int _InteractorCountID = Shader.PropertyToID("_InteractorCount");

        private void OnEnable()
        {
            // Register with GrassInstanceRenderer system
            GrassInstanceRenderer.RegisterInteractor(transform);
            _lastPosition = GetInteractionPoint();
            _currentRadius = bendRadius;
        }

        private void OnDisable()
        {
            GrassInstanceRenderer.UnregisterInteractor(transform);

            if (grassParticles != null && grassParticles.isPlaying)
                grassParticles.Stop();
        }

        private void Update()
        {
            Vector3 currentPos = GetInteractionPoint();

            // Calculate speed
            _currentSpeed = Vector3.Distance(currentPos, _lastPosition) / Time.deltaTime;
            _lastPosition = currentPos;

            // Speed-based radius scaling
            if (speedScaling)
            {
                float speedFactor = Mathf.Clamp01(_currentSpeed / maxSpeed);
                float targetRadius = bendRadius * Mathf.Lerp(1f, maxSpeedRadiusMultiplier, speedFactor);
                _currentRadius = Mathf.Lerp(_currentRadius, targetRadius, Time.deltaTime * 8f);
            }
            else
            {
                _currentRadius = bendRadius;
            }

            // Particles
            UpdateParticles();
        }

        /// <summary>
        /// Returns the world-space interaction point (feet position)
        /// </summary>
        public Vector3 GetInteractionPoint()
        {
            if (overridePoint != null)
                return overridePoint.position;

            return transform.position + Vector3.down * groundOffset;
        }

        /// <summary>
        /// Returns the current effective radius (including speed scaling)
        /// </summary>
        public float GetEffectiveRadius()
        {
            return _currentRadius;
        }

        /// <summary>
        /// Returns Vector4 for shader: xyz = position, w = radius
        /// </summary>
        public Vector4 GetShaderData()
        {
            Vector3 pos = GetInteractionPoint();
            return new Vector4(pos.x, pos.y, pos.z, _currentRadius);
        }

        private void UpdateParticles()
        {
            if (grassParticles == null) return;

            _particleCooldown -= Time.deltaTime;

            if (_currentSpeed > particleSpeedThreshold && _particleCooldown <= 0f)
            {
                // Check if we're actually near grass (simple ground raycast)
                if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, 2f))
                {
                    // Position particles at ground
                    grassParticles.transform.position = hit.point;

                    if (!grassParticles.isPlaying)
                        grassParticles.Play();
                }
            }
            else if (_currentSpeed < particleSpeedThreshold * 0.5f)
            {
                if (grassParticles.isPlaying)
                    grassParticles.Stop();
            }
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 pos = Application.isPlaying ? GetInteractionPoint() : transform.position - Vector3.up * groundOffset;
            float radius = Application.isPlaying ? _currentRadius : bendRadius;

            // Interaction radius
            Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.2f);
            Gizmos.DrawSphere(pos, radius);
            Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(pos, radius);

            // Interaction point
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(pos, 0.1f);
        }
    }
}
