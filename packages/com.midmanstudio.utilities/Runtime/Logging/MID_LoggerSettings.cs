using UnityEngine;

namespace MidManStudio.Core.Logging
{
    [CreateAssetMenu(fileName = "MID_LoggerSettings", menuName = "MidManStudio/Logger Settings")]
    public class MID_LoggerSettings : ScriptableObject
    {
        [SerializeField] private MID_LogLevel _defaultLogLevel = MID_LogLevel.Debug;

        public MID_LogLevel DefaultLogLevel
        {
            get => _defaultLogLevel;
            set => _defaultLogLevel = value;
        }
    }
}