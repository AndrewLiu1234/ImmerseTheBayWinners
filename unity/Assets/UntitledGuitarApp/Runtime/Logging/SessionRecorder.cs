// SessionRecorder.cs: records a session as JSON lines, one object per line, to
//     <Application.persistentDataPath>/sessions/session-<yyyyMMdd-HHmmss>.jsonl
// (scripts/pull-session.sh copies that folder to logs/sessions/). This is the SKELETON: it owns the file, the queue and the
// writer thread. What goes in is up to the systems that record:
//   * while recording, every UGALog line is also appended here (without the "[UGA] " prefix), so anything the other systems
//     log with UGALog.Event(...) ends up in the session file;
//   * high-rate data that must NOT go to the Unity console (head pose, fps, audio frames) calls Record(...) or Write(...).
//
// Threading: Start() and Stop() are for the MAIN thread (they read Application.persistentDataPath, which is main-thread
// only: its managed binding is not marked thread safe). Write(...) and Record(...) are safe from any thread: they hand the
// line to a bounded queue drained by a background writer thread and NEVER block the caller. When the queue is full the line
// is dropped and counted (DroppedLines; also written into the closing "session_end" line).
//
// Lines you pass to Write must be one JSON object each; use UGALog.FormatJson(...) or Record(...) to build them.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Logging
{
    public static class SessionRecorder
    {
        /// <summary>Lines that may wait for the disk before new ones are dropped.</summary>
        public const int DefaultCapacity = 8192;
        /// <summary>How long Stop() waits for the writer to drain.</summary>
        public const int StopTimeoutMs = 3000;

        static readonly object s_gate = new object();
        static volatile SessionLineWriter s_writer;       // null when not recording
        static string s_path;
        static long s_lastDropped;
        static long s_lastWritten;
        static bool s_quitHooked;

        public static bool IsRecording { get { return s_writer != null; } }

        /// <summary>Path of the current session file, or of the last one after Stop(); null before the first Start().</summary>
        public static string CurrentPath { get { return s_path; } }

        /// <summary>Lines dropped because the queue was full, in the current (or last) session.</summary>
        public static long DroppedLines { get { SessionLineWriter w = s_writer; return w != null ? w.Dropped : s_lastDropped; } }

        /// <summary>Lines handed to the file, in the current (or last) session.</summary>
        public static long WrittenLines { get { SessionLineWriter w = s_writer; return w != null ? w.Written : s_lastWritten; } }

        /// <summary>Starts a session in &lt;persistentDataPath&gt;/sessions. Main thread. Returns true when recording (also if it already was).</summary>
        public static bool Start()
        {
            return Start(Path.Combine(Application.persistentDataPath, "sessions"));
        }

        /// <summary>Starts a session in <paramref name="directory"/> (created if missing). Main thread. Never throws.</summary>
        public static bool Start(string directory)
        {
            string path;
            lock (s_gate)
            {
                if (s_writer != null) return true;
                try
                {
                    Directory.CreateDirectory(directory);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    FileStream stream = null;
                    path = null;
                    for (int attempt = 1; attempt <= 100 && stream == null; attempt++)
                    {
                        string suffix = attempt == 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
                        path = Path.Combine(directory, "session-" + stamp + suffix + ".jsonl");
                        try
                        {
                            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        }
                        catch (IOException)
                        {
                            if (!File.Exists(path)) throw;          // a real I/O problem; "exists" just means: try the next suffix
                        }
                    }
                    if (stream == null) throw new IOException("no free session file name in " + directory);
                    StreamWriter text = new StreamWriter(stream, new UTF8Encoding(false));
                    text.NewLine = "\n";
                    s_writer = new SessionLineWriter(text, DefaultCapacity);
                    s_path = path;
                }
                catch (Exception e)
                {
                    UGALog.Warn("session_start_failed", e.GetType().Name + ": " + e.Message);
                    return false;
                }
                if (!s_quitHooked)
                {
                    Application.quitting += OnQuitting;         // https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-quitting.html
                    s_quitHooked = true;                        // raised when the player quits and when Play mode ends
                }
            }
            UGALog.Configure();
            Write(UGALog.FormatJson(UGALog.Now, "session_start",
                "path", path, "unity", Application.unityVersion, "platform", Application.platform.ToString()));
            return true;
        }

        /// <summary>Writes the closing "session_end" line, drains the queue and closes the file. Main thread. Safe to call twice.</summary>
        public static void Stop()
        {
            SessionLineWriter w;
            lock (s_gate)
            {
                w = s_writer;
                if (w == null) return;
                if (s_quitHooked)
                {
                    Application.quitting -= OnQuitting;
                    s_quitHooked = false;
                }
            }
            w.TryWriteReserved(UGALog.FormatJson(UGALog.Now, "session_end",
                "accepted", w.Accepted, "dropped", w.Dropped));
            lock (s_gate) { s_writer = null; }                   // from here on Write() is refused
            bool drained = w.Close(StopTimeoutMs);
            s_lastDropped = w.Dropped;
            s_lastWritten = w.Written;
            if (w.Dropped > 0)
                UGALog.Warn("session_dropped", w.Dropped + " line(s) were dropped because the session writer queue was full");
            if (!drained)
                UGALog.Warn("session_stop_timeout", "the session file was still being written after " + StopTimeoutMs + " ms");
            if (w.Error != null)
                UGALog.Warn("session_write_failed", w.Error);
        }

        /// <summary>
        /// Appends one JSON object (no newline) to the session file. Any thread, never blocks. Returns false when not
        /// recording or when the line was dropped because the queue is full.
        /// </summary>
        public static bool Write(string jsonLine)
        {
            SessionLineWriter w = s_writer;
            return w != null && w.TryWrite(jsonLine);
        }

        // Convenience: build the line with the same JSON formatting as UGALog (so "t" is the same clock) and write it. These
        // do nothing, and allocate nothing but the boxing at the call site, while not recording.
        public static bool Record(string evt)
        {
            return IsRecording && Write(UGALog.FormatJson(UGALog.Now, evt));
        }

        public static bool Record(string evt, string k1, object v1)
        {
            return IsRecording && Write(UGALog.FormatJson(UGALog.Now, evt, k1, v1));
        }

        public static bool Record(string evt, string k1, object v1, string k2, object v2)
        {
            return IsRecording && Write(UGALog.FormatJson(UGALog.Now, evt, k1, v1, k2, v2));
        }

        public static bool Record(string evt, string k1, object v1, string k2, object v2, string k3, object v3)
        {
            return IsRecording && Write(UGALog.FormatJson(UGALog.Now, evt, k1, v1, k2, v2, k3, v3));
        }

        static void OnQuitting()
        {
            Stop();
        }
    }
}
