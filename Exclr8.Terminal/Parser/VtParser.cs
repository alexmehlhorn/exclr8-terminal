using System;
using System.Text;

namespace Exclr8.Terminal.Parser;

/// <summary>
/// VT500-series escape sequence parser, ported from xterm.js's
/// <c>EscapeSequenceParser</c> (which itself follows Paul Williams'
/// state diagram at vt100.net/emu/dec_ansi_parser). Feeds bytes in,
/// dispatches high-level actions through <see cref="IParserActions"/>.
///
/// <para>This is a "naive but correct" implementation: state transitions
/// are big switch blocks per incoming byte, no precomputed transition
/// table. Plenty fast for PTY output rates — we'll profile and
/// table-ify only if real workloads show the hotspot.</para>
/// </summary>
public sealed class VtParser
{
    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,           // we consume-and-ignore DCS so vim/tmux don't break
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,     // ditto — consumed until ST
    }

    private readonly IParserActions _actions;

    private State _state = State.Ground;
    private readonly int[] _params = new int[32];
    private int _paramCount;
    private int _currentParam;
    // CSI colon-subparameter tracking. Two distinct modes:
    //   _inSubParam:    we're swallowing a sub-param cluster that
    //                   modifies the current primary (e.g. the '3' in
    //                   `\e[4:3m` — curly underline). Digits & further
    //                   colons are ignored until ';' / final byte.
    //   _inExtColorRun: we're inside an extended-colour run introduced
    //                   by SGR 38 or 48, where colons legitimately
    //                   separate colour-spec components. Colons in
    //                   this mode push params like ';' does. Reset on
    //                   ';' or the final byte.
    // Reset on state re-entry.
    private bool _inSubParam;
    private bool _inExtColorRun;
    private char _privatePrefix;
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _oscBuffer = new();

    // UTF-8 accumulator — printable codepoints that span multiple bytes
    // are assembled here before dispatch to Print().
    private int _utf8State;
    private int _utf8Accum;

    public VtParser(IParserActions actions) { _actions = actions; }

    public void Reset()
    {
        _state = State.Ground;
        _paramCount = 0;
        _currentParam = 0;
        _privatePrefix = (char)0;
        _intermediates.Clear();
        _oscBuffer.Clear();
        _utf8State = 0;
        _utf8Accum = 0;
    }

    public void Parse(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            // Anywhere-transitions (from the VT500 diagram): ESC / CAN /
            // SUB / 0x18 / 0x1A / 0x1B short-circuit almost every state
            // back to escape or ground. We check these first, except in
            // OSC/DCS which accumulate until their own terminators.
            if (_state != State.OscString && _state != State.DcsPassthrough
                && _state != State.DcsIgnore && _state != State.SosPmApcString)
            {
                if (b == 0x18 || b == 0x1A)
                {
                    _actions.Execute(b);
                    _state = State.Ground;
                    continue;
                }
                if (b == 0x1B)
                {
                    EnterEscape();
                    continue;
                }
            }

            switch (_state)
            {
                case State.Ground:         Ground(b); break;
                case State.Escape:         Escape(b); break;
                case State.EscapeIntermediate: EscapeIntermediate(b); break;
                case State.CsiEntry:       CsiEntry(b); break;
                case State.CsiParam:       CsiParam(b); break;
                case State.CsiIntermediate: CsiIntermediate(b); break;
                case State.CsiIgnore:      CsiIgnore(b); break;
                case State.OscString:      OscString(b); break;
                case State.DcsEntry:       DcsEntry(b); break;
                case State.DcsPassthrough: DcsPassthrough(b); break;
                case State.DcsIgnore:      DcsIgnore(b); break;
                case State.SosPmApcString: SosPmApcString(b); break;
            }
        }
    }

    // ------------------------------------------------------------------
    // GROUND: print printable, execute C0.
    // ------------------------------------------------------------------

    private void Ground(byte b)
    {
        if (b < 0x20 || b == 0x7F) { _actions.Execute(b); return; }

        // UTF-8 multibyte assembly. Leading byte bit-patterns:
        //   0xxxxxxx → 1-byte (ASCII)
        //   110xxxxx → 2-byte header
        //   1110xxxx → 3-byte header
        //   11110xxx → 4-byte header
        //   10xxxxxx → continuation
        if (_utf8State > 0)
        {
            if ((b & 0xC0) != 0x80) { _utf8State = 0; _utf8Accum = 0; } // bad sequence, resync
            else
            {
                _utf8Accum = (_utf8Accum << 6) | (b & 0x3F);
                _utf8State--;
                if (_utf8State == 0)
                {
                    _actions.Print(_utf8Accum);
                    _utf8Accum = 0;
                }
                return;
            }
        }

        if ((b & 0x80) == 0) { _actions.Print(b); return; }
        if ((b & 0xE0) == 0xC0) { _utf8State = 1; _utf8Accum = b & 0x1F; return; }
        if ((b & 0xF0) == 0xE0) { _utf8State = 2; _utf8Accum = b & 0x0F; return; }
        if ((b & 0xF8) == 0xF0) { _utf8State = 3; _utf8Accum = b & 0x07; return; }

        // Lone continuation or 5/6-byte lead — skip.
    }

    // ------------------------------------------------------------------
    // ESCAPE: after 0x1B. Next byte decides what kind of sequence.
    // ------------------------------------------------------------------

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediates.Clear();
    }

    private void Escape(byte b)
    {
        if (b < 0x20)       { _actions.Execute(b); return; }       // C0 stays in escape (7-bit spec)
        if (b == 0x7F)      { return; }                             // ignore DEL in escape

        if (b >= 0x20 && b <= 0x2F) { _intermediates.Append((char)b); _state = State.EscapeIntermediate; return; }

        switch (b)
        {
            case 0x50: EnterDcsEntry(); return;        // DCS
            case 0x58: _state = State.SosPmApcString; return; // SOS
            case 0x5B: EnterCsiEntry(); return;        // CSI
            case 0x5D: EnterOsc(); return;             // OSC
            case 0x5E: _state = State.SosPmApcString; return; // PM
            case 0x5F: _state = State.SosPmApcString; return; // APC
        }

        // Plain ESC + final. 0x30-0x7E
        if (b >= 0x30 && b <= 0x7E)
        {
            _actions.EscDispatch((char)b, _intermediates.ToString());
            _state = State.Ground;
        }
    }

    private void EscapeIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { _intermediates.Append((char)b); return; }
        if (b >= 0x30 && b <= 0x7E)
        {
            _actions.EscDispatch((char)b, _intermediates.ToString());
            _state = State.Ground;
            return;
        }
        if (b < 0x20) { _actions.Execute(b); return; }
    }

    // ------------------------------------------------------------------
    // CSI: ESC [ params intermediates final.
    // ------------------------------------------------------------------

    private void EnterCsiEntry()
    {
        _state = State.CsiEntry;
        _paramCount = 0;
        _currentParam = 0;
        _inSubParam = false;
        _inExtColorRun = false;
        _privatePrefix = (char)0;
        _intermediates.Clear();
    }

    private void CsiEntry(byte b)
    {
        if (b < 0x20)       { _actions.Execute(b); return; }
        if (b == 0x7F)      { return; }

        if (b >= 0x30 && b <= 0x39) { _currentParam = b - 0x30; _state = State.CsiParam; return; }
        if (b == 0x3B)               { PushParam(); _state = State.CsiParam; return; }
        if (b >= 0x3C && b <= 0x3F)  { _privatePrefix = (char)b; _state = State.CsiParam; return; }
        if (b >= 0x20 && b <= 0x2F)  { _intermediates.Append((char)b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)  { DispatchCsi((char)b); return; }
    }

    /// <summary>Upper bound on a single CSI parameter. Matches xterm's
    /// MAX_PARAM = 0x3FFF — big enough for anything a real app emits,
    /// small enough to prevent integer overflow from malicious input
    /// like CSI 9999999999999999999A.</summary>
    private const int ParamMax = 0x7FFFFFFF / 10;

    private void CsiParam(byte b)
    {
        if (b < 0x20)       { _actions.Execute(b); return; }
        if (b == 0x7F)      { return; }

        if (b >= 0x30 && b <= 0x39)
        {
            // Digits inside a sub-parameter don't modify the primary
            // param — we already pushed the primary when the ':' was
            // seen. Skip over the sub-param content entirely.
            if (_inSubParam) return;
            if (_currentParam < ParamMax)
                _currentParam = _currentParam * 10 + (b - 0x30);
            return;
        }
        if (b == 0x3B)
        {
            // Semicolon: push the current primary (unless we were mid
            // sub-param — the primary was already pushed when ':'
            // opened it), then leave both colon modes.
            if (!_inSubParam) PushParam();
            _inSubParam = false;
            _inExtColorRun = false;
            return;
        }
        if (b == 0x3A)
        {
            // Colons have two distinct meanings in SGR:
            //   (a) Components of an extended-colour run introduced by
            //       SGR 38 or 48 — e.g. `\e[38:2::R:G:Bm`. These need
            //       to surface as primary params so ApplyExtColor
            //       receives them. Latch _inExtColorRun on the FIRST
            //       colon in the run (when the just-seen primary is
            //       38 or 48) and keep treating colons like
            //       semicolons until ';' or the final byte.
            //   (b) Style sub-params on any other SGR — e.g.
            //       `\e[4:3m` (curly underline). Swallow the whole
            //       sub-param cluster so sub-param 3 doesn't become a
            //       stray SGR-3 italic.
            if (_inExtColorRun)
            {
                PushParam();
                return;
            }
            // Haven't entered an ext-colour run yet. Look at the
            // primary we're about to push: if it's 38/48, latch the
            // run mode.
            if (!_inSubParam && (_currentParam == 38 || _currentParam == 48))
            {
                PushParam();
                _inExtColorRun = true;
                return;
            }
            if (!_inSubParam) { PushParam(); _inSubParam = true; }
            return;
        }
        if (b >= 0x20 && b <= 0x2F)
        {
            if (!_inSubParam) PushParam();
            _inSubParam = false; _inExtColorRun = false;
            _intermediates.Append((char)b);
            _state = State.CsiIntermediate;
            return;
        }
        if (b >= 0x3C && b <= 0x3F)  { _state = State.CsiIgnore; return; } // private modifier mid-params
        if (b >= 0x40 && b <= 0x7E)
        {
            if (!_inSubParam) PushParam();
            _inSubParam = false; _inExtColorRun = false;
            DispatchCsi((char)b);
            return;
        }
    }

    private void CsiIntermediate(byte b)
    {
        if (b < 0x20)       { _actions.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { _intermediates.Append((char)b); return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
    }

    private void CsiIgnore(byte b)
    {
        if (b < 0x20)       { _actions.Execute(b); return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.Ground; return; }
    }

    private void PushParam()
    {
        if (_paramCount < _params.Length) _params[_paramCount++] = _currentParam;
        _currentParam = 0;
    }

    private void DispatchCsi(char final)
    {
        // Ensure there's at least one param recorded (handles bare `ESC [ H`).
        if (_paramCount == 0) _params[_paramCount++] = _currentParam;
        var copy = new int[_paramCount];
        Array.Copy(_params, copy, _paramCount);
        _actions.CsiDispatch(final, copy, _intermediates.ToString(), _privatePrefix);
        _state = State.Ground;
    }

    // ------------------------------------------------------------------
    // OSC: ESC ] payload terminator. Terminator is BEL (0x07) or ST (ESC \).
    // ------------------------------------------------------------------

    private void EnterOsc()
    {
        _state = State.OscString;
        _oscBuffer.Clear();
    }

    /// <summary>Hard cap on accumulated OSC payload length (bytes).
    /// Anything past this is silently dropped until the sequence
    /// terminator arrives — prevents a runaway emitter from blowing
    /// memory.</summary>
    private const int OscMaxLength = 64 * 1024;

    private void OscString(byte b)
    {
        if (b == 0x07) { _actions.OscDispatch(_oscBuffer.ToString()); _state = State.Ground; return; }
        if (b == 0x1B) { /* will be followed by \ for ST — we just end here for simplicity */
            _actions.OscDispatch(_oscBuffer.ToString());
            _state = State.Escape;
            _intermediates.Clear();
            return;
        }
        if (b < 0x20) return; // ignore other C0 in OSC
        if (_oscBuffer.Length >= OscMaxLength) return;
        _oscBuffer.Append((char)b);
    }

    // ------------------------------------------------------------------
    // DCS / SOS / PM / APC — consume until ST, no dispatch.
    // ------------------------------------------------------------------

    private void EnterDcsEntry() => _state = State.DcsEntry;

    private void DcsEntry(byte b)
    {
        if (b < 0x20) return;
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        if (b == 0x7F) return;
        // Anything else → eat until ST.
        _state = State.DcsPassthrough;
    }

    private void DcsPassthrough(byte b)
    {
        if (b == 0x1B) { _state = State.Escape; _intermediates.Clear(); return; }
        // Drop payload — we don't implement any DCS sequences yet.
    }

    private void DcsIgnore(byte b)
    {
        if (b == 0x1B) { _state = State.Escape; _intermediates.Clear(); return; }
    }

    private void SosPmApcString(byte b)
    {
        if (b == 0x1B) { _state = State.Escape; _intermediates.Clear(); return; }
        if (b == 0x07) { _state = State.Ground; return; } // lenient: BEL also ends
    }
}
