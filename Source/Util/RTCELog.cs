namespace RimTalkCustomEvents.Util
{
    /// <summary>
    /// Logging helper. Named RTCELog rather than Log so it never collides with Verse.Log
    /// in files that use both namespaces.
    /// </summary>
    public static class RTCELog
    {
        private const string Prefix = "[RimTalk Custom Events] ";

        /// <summary>Always shown. Use sparingly — startup and genuine problems only.</summary>
        public static void Message(string message)
        {
            Verse.Log.Message(Prefix + message);
        }

        public static void Warning(string message)
        {
            Verse.Log.Warning(Prefix + message);
        }

        public static void Error(string message)
        {
            Verse.Log.Error(Prefix + message);
        }

        /// <summary>Only shown when debug logging is enabled in mod settings.</summary>
        public static void Debug(string message)
        {
            if (RimTalkCustomEventsMod.Settings != null && RimTalkCustomEventsMod.Settings.debugLogging)
            {
                Verse.Log.Message(Prefix + message);
            }
        }

        /// <summary>
        /// Warns once per unique key. For problems that would otherwise spam every tick.
        /// </summary>
        public static void WarnOnce(string message, int key)
        {
            Verse.Log.WarningOnce(Prefix + message, key);
        }
    }
}
