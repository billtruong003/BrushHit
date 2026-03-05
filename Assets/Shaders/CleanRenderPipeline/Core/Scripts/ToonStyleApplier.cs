using UnityEngine;

namespace CleanRender
{
    /// <summary>
    /// Apply ToonStyleConfig global shader params at runtime.
    /// Place on a persistent GameObject in the scene.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class ToonStyleApplier : MonoBehaviour
    {
        [SerializeField] private ToonStyleConfig config;
        [SerializeField] private bool applyEveryFrame = false; // true for runtime style switching

        private void Awake()
        {
            if (config != null) config.Apply();
        }

        private void Update()
        {
            if (applyEveryFrame && config != null)
                config.Apply();
        }

        /// <summary>
        /// Switch style at runtime (e.g., day → night mood)
        /// </summary>
        public void SwitchStyle(ToonStyleConfig newConfig, float transitionTime = 0f)
        {
            config = newConfig;
            config.Apply();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (config != null) config.Apply();
        }
#endif
    }
}
