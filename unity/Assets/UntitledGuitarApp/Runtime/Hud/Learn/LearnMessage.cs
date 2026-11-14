using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// A message that appears, stays and fades: the shared behaviour of the tip line, the strum verdict and the finger
    /// prompt. It eases in (readable on the first frame), changes at most 3 times a second however often it is asked
    /// (<see cref="ChangeGate"/>), can hide itself after a reading time, and does no work at all while nothing moves.
    /// Subclasses supply the words and the layout (<see cref="ApplyContent"/>) and the fading (<see cref="ApplyVisual"/>).
    /// </summary>
    public abstract class LearnMessage : LearnWidget
    {
        HudFader _fader;
        ChangeGate _gate;
        bool _wantVisible;
        bool _contentDirty;
        float _pendingHold = float.PositiveInfinity;
        float _holdLeft = float.PositiveInfinity;
        Transform _content;

        /// <summary>True when the message is meant to be on screen (it may still be easing in, or waiting for the 3 Hz gate).</summary>
        public bool IsShowing { get { return _wantVisible; } }

        /// <summary>True while anything of the message is on screen, including while it fades out.</summary>
        public bool IsVisible { get { return _fader.IsVisible; } }

        /// <summary>The object that holds the message's parts; it is scaled as one for the ease-in.</summary>
        protected Transform Content { get { return _content; } }

        protected sealed override void Build()
        {
            var content = new GameObject("Content");
            content.transform.SetParent(transform, false);
            _content = content.transform;
            BuildContent();
            ApplyVisual(0f);
        }

        /// <summary>
        /// Asks for the message to be shown with the content the subclass has just stored. It is on screen on the next
        /// Tick unless this element changed less than 0.34 s ago, in which case it follows as soon as the gate opens.
        /// <paramref name="holdSeconds"/> = how long it stays before fading by itself (infinity = until Hide).
        /// </summary>
        protected void RequestShow(float holdSeconds)
        {
            _wantVisible = true;
            _contentDirty = true;
            _pendingHold = holdSeconds > 0f ? holdSeconds : float.PositiveInfinity;
            _gate.Request();
            OnTick(0f);   // if the gate is open the message is on screen for this frame's render, whatever the script order
        }

        /// <summary>Fades the message out (after the 3 Hz gate, so a message can never just blink).</summary>
        public void Hide()
        {
            EnsureBuilt();
            if (!_wantVisible) return;
            _wantVisible = false;
            _contentDirty = false;
            _gate.Request();
            OnHidden();
            OnTick(0f);
        }

        /// <summary>Removes the message at once, with no fade. For when another message takes its place.</summary>
        public void HideNow()
        {
            EnsureBuilt();
            bool was = _wantVisible;
            _wantVisible = false;
            _contentDirty = false;
            _gate.Cancel();
            _holdLeft = float.PositiveInfinity;
            if (_fader.IsVisible || _fader.Target)
            {
                _fader.HideNow();
                _fader.Tick(0f);
                ApplyVisual(0f);
            }
            if (was) OnHidden();
        }

        /// <summary>Jumps to the settled look (fully shown or fully hidden), skipping the animation. For previews.</summary>
        public void Settle()
        {
            EnsureBuilt();
            _gate.Cancel();
            if (_wantVisible)
            {
                if (_contentDirty)
                {
                    ApplyContent();
                    _contentDirty = false;
                }
                _fader.ShowNow();
                _holdLeft = _pendingHold;
            }
            else
            {
                _fader.HideNow();
            }
            _fader.Tick(0f);
            _content.localScale = Vector3.one;
            ApplyVisual(_fader.Opacity);
        }

        protected sealed override void OnTick(float dt)
        {
            if (_gate.Tick(dt))
            {
                if (_wantVisible)
                {
                    if (_contentDirty)
                    {
                        ApplyContent();
                        _contentDirty = false;
                    }
                    if (_fader.Target) _fader.Pulse();
                    else _fader.Show();
                    _holdLeft = _pendingHold;
                }
                else
                {
                    _fader.Hide();
                    _holdLeft = float.PositiveInfinity;
                }
            }

            if (_fader.Target && !float.IsPositiveInfinity(_holdLeft))
            {
                _holdLeft -= dt;
                if (_holdLeft <= 0f)
                {
                    _holdLeft = float.PositiveInfinity;
                    if (_wantVisible)
                    {
                        _wantVisible = false;
                        _gate.Request();
                        OnHidden();
                    }
                }
            }

            if (_fader.Tick(dt))
            {
                float s = _fader.Scale;
                _content.localScale = new Vector3(s, s, s);
                ApplyVisual(_fader.Opacity);
            }
        }

        /// <summary>Creates the parts under <see cref="Content"/>. Called once.</summary>
        protected abstract void BuildContent();

        /// <summary>Puts the stored content on screen: words, shapes, colours, positions. Called only when the content changes.</summary>
        protected abstract void ApplyContent();

        /// <summary>Sets the brightness (0..1) of every part. Called only while the message is easing in or fading.</summary>
        protected abstract void ApplyVisual(float opacity);

        /// <summary>Called when the message is asked to go away (by Hide, HideNow or its reading time running out).</summary>
        protected virtual void OnHidden()
        {
        }
    }
}
