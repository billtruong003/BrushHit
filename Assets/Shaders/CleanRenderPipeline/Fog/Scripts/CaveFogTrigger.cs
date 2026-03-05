using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace CleanRender
{
    /// <summary>
    /// CaveFogTrigger: Khi player đi vào trigger zone:
    /// 1. Fog plane fade biến mất (đã xử lý trong shader bằng distance)
    /// 2. Bật/tắt renderer groups (hide outside khi inside, hide inside khi outside)
    /// 3. Smooth transition via fog density lerp
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class CaveFogTrigger : MonoBehaviour
    {
        [Header("━━━ References ━━━")]
        [Tooltip("Fog renderers that should disappear when player enters")]
        [SerializeField] private Renderer[] fogRenderers;

        [Tooltip("Objects OUTSIDE cave (disable when player is inside)")]
        [SerializeField] private GameObject[] outsideObjects;

        [Tooltip("Objects INSIDE cave (enable when player is inside)")]
        [SerializeField] private GameObject[] insideObjects;

        [Header("━━━ Transition ━━━")]
        [SerializeField] private float transitionSpeed = 3f;
        [SerializeField] private float fogFadeDuration = 1.5f;

        [Header("━━━ Player Detection ━━━")]
        [SerializeField] private string playerTag = "Player";
        [SerializeField] private LayerMask playerLayer = 1;

        private bool _playerInside;
        private float _transitionProgress; // 0 = outside, 1 = inside
        private MaterialPropertyBlock _fogBlock;
        private static readonly int _FogDensityID = Shader.PropertyToID("_FogDensity");

        private void Awake()
        {
            _fogBlock = new MaterialPropertyBlock();

            // Ensure trigger
            var col = GetComponent<Collider>();
            col.isTrigger = true;

            // Initial state: outside
            SetState(false, true);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (IsPlayer(other))
            {
                _playerInside = true;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (IsPlayer(other))
            {
                _playerInside = false;
            }
        }

        private bool IsPlayer(Collider col)
        {
            if (!string.IsNullOrEmpty(playerTag) && col.CompareTag(playerTag)) return true;
            return ((1 << col.gameObject.layer) & playerLayer) != 0;
        }

        private void Update()
        {
            float target = _playerInside ? 1f : 0f;
            _transitionProgress = Mathf.MoveTowards(_transitionProgress, target, Time.deltaTime * transitionSpeed);

            // ── Fog Fade ──
            float fogDensity = 1f - Mathf.Clamp01(_transitionProgress / Mathf.Max(fogFadeDuration * transitionSpeed, 0.01f));
            foreach (var fogRenderer in fogRenderers)
            {
                if (fogRenderer == null) continue;
                fogRenderer.GetPropertyBlock(_fogBlock);
                _fogBlock.SetFloat(_FogDensityID, fogDensity);
                fogRenderer.SetPropertyBlock(_fogBlock);
                fogRenderer.enabled = fogDensity > 0.01f;
            }

            // ── Object Toggle ──
            bool fullyInside = _transitionProgress > 0.9f;
            bool fullyOutside = _transitionProgress < 0.1f;

            if (fullyInside)
            {
                SetState(true, false);
            }
            else if (fullyOutside)
            {
                SetState(false, true);
            }
            // During transition: show both
            else
            {
                SetState(true, true);
            }
        }

        private void SetState(bool showInside, bool showOutside)
        {
            foreach (var obj in insideObjects)
                if (obj) obj.SetActive(showInside);

            foreach (var obj in outsideObjects)
                if (obj) obj.SetActive(showOutside);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 0.2f, 0.8f, 0.3f);
            var col = GetComponent<Collider>();
            if (col is BoxCollider box)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.DrawWireCube(box.center, box.size);
            }
            else if (col is SphereCollider sphere)
            {
                Gizmos.DrawSphere(transform.position + sphere.center, sphere.radius);
                Gizmos.DrawWireSphere(transform.position + sphere.center, sphere.radius);
            }
        }
    }
}
