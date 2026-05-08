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
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,     // payload between DCS framing and ST
        DcsIgnore,
        SosPmApcString,     // consumed until ST
    }

    private readonly IParserActions _actions;

    private State _state = State.Ground;
    private readonly int[] _params = new int[32];
    private int _paramCount;
    private int _currentParam;
    // CSI colon-subparameter tracking. Two distinct modes:
    //   _inSubParam:    we're inside a sub-param cluster that modifies
    //                   the current primary (e.g. the '3' in `\e[4:3m`
    //                   — curly underline). Digits accumulate into
    //                   _subParam, which is surfaced as part of the
    //                   sub-param span on dispatch. Multiple colons
    //                   in one cluster aren't useful for any SGR sub
    //                   we care about (4:N) so we keep just the first.
    //   _inExtColorRun: we're inside an extended-colour run introduced
    //                   by SGR 38, 48, or 58, where colons legitimately
    //                   separate colour-spec components. Colons in
    //                   this mode push params like ';' does. Reset on
    //                   ';' or the final byte.
    // Reset on state re-entry.
    private bool _inSubParam;
    private bool _inExtColorRun;
    private int  _subParam;

    // Sub-params parallel to _params, keyed by primary index. Slot i
    // holds the colon-sub for _params[i] (0 if none). Sized to the
    // primaries array so they always match up.
    private readonly int[] _subParams = new int[32];
    private char _privatePrefix;
    private readonly StringBuilder _intermediates = new();
    // OSC payload accumulator — kept as a raw byte buffer so multi-
    // byte UTF-8 codepoints (terminal titles like "✳ Claude Code")
    // can be reassembled cleanly at dispatch time instead of being
    // smeared across one char per byte (Latin-1-style mojibake). The
    // matching char buffer is sized lazily on dispatch from the
    // decoded char count and reused across OSCs. Grows up to
    // OscMaxLength; consumers that need to retain the payload
    // (title/URL storage) materialise a string themselves.
    private byte[] _oscBuffer = new byte[256];
    private int _oscLen;
    private char[] _oscCharBuffer = new char[256];

    // DCS framing + payload — parameters and payload are dispatched
    // together when ST arrives. Allocated lazily; same hard cap as OSC
    // applies to the payload so a runaway emitter can't blow memory.
    private readonly int[] _dcsParams = new int[16];
    private int _dcsParamCount;
    private int _dcsCurrentParam;
    private char _dcsPrivatePrefix;
    private readonly StringBuilder _dcsIntermediates = new();
    private char _dcsFinal;
    // DCS payload accumulator — raw byte buffer for the same reason as
    // the OSC accumulator above: a vendor DCS handler registered via
    // RegisterDcsHandler may carry UTF-8 (a label, a comment), which
    // would smear into Latin-1-style mojibake if we accumulated one
    // char per byte. Decoded into _dcsCharBuffer at dispatch.
    private byte[] _dcsBuffer = new byte[256];
    private int _dcsLen;
    private char[] _dcsCharBuffer = new char[256];

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
        _oscLen = 0;
        _utf8State = 0;
        _utf8Accum = 0;
        _dcsParamCount = 0;
        _dcsCurrentParam = 0;
        _dcsPrivatePrefix = (char)0;
        _dcsIntermediates.Clear();
        _dcsFinal = (char)0;
        _dcsLen = 0;
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
            // C1 control bytes (0x90 DCS, 0x98/0x9E/0x9F SOS/PM/APC,
            // 0x9B CSI, 0x9D OSC) act the same way — they're the 8-bit
            // form of the ESC <x> sequence starts. Guarded by _utf8State
            // because those byte values are valid UTF-8 continuation
            // bytes mid-rune and must not hijack them.
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
                if (_utf8State == 0)
                {
                    if (b == 0x90) { EnterDcsEntry(); continue; }
                    if (b == 0x9B) { EnterCsiEntry(); continue; }
                    if (b == 0x9D) { EnterOsc();      continue; }
                    if (b == 0x98 || b == 0x9E || b == 0x9F)
                    {
                        _state = State.SosPmApcString;
                        continue;
                    }
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
                case State.DcsParam:       DcsParam(b); break;
                case State.DcsIntermediate: DcsIntermediate(b); break;
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
                    int rune = _utf8Accum;
                    _utf8Accum = 0;
                    // Reject surrogates (U+D800..U+DFFF) and runes
                    // above the Unicode range (> U+10FFFF). Both crash
                    // char.ConvertFromUtf32 downstream in the
                    // renderer / search, and are never valid UTF-8
                    // output anyway — replace with U+FFFD so a
                    // hostile or buggy byte source can't take us down.
                    if ((uint)rune > 0x10FFFF || (rune >= 0xD800 && rune <= 0xDFFF))
                        rune = 0xFFFD;
                    _actions.Print(rune);
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
        _subParam = 0;
        // Sub-params are sparsely populated; clear the whole array so
        // last frame's values don't leak into this one.
        Array.Clear(_subParams, 0, _subParams.Length);
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
            if (_inSubParam)
            {
                // Accumulate the sub-param value so we can attach it
                // to the just-pushed primary on cluster end.
                if (_subParam < ParamMax)
                    _subParam = _subParam * 10 + (b - 0x30);
                return;
            }
            if (_currentParam < ParamMax)
                _currentParam = _currentParam * 10 + (b - 0x30);
            return;
        }
        if (b == 0x3B)
        {
            // Semicolon: push the current primary (unless we were mid
            // sub-param — the primary was already pushed when ':'
            // opened it). When leaving a sub-param cluster, attach the
            // accumulated sub-param value to the primary that owns it.
            if (_inSubParam) FinalizeSubParam();
            else             PushParam();
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
            //       38 / 48 / 58) and keep treating colons like
            //       semicolons until ';' or the final byte.
            //   (b) Style sub-params on any other SGR — e.g.
            //       `\e[4:3m` (curly underline). The first sub-param
            //       value is captured into _subParams[primaryIdx] and
            //       attached when the cluster ends.
            if (_inExtColorRun)
            {
                PushParam();
                return;
            }
            // Haven't entered an ext-colour run yet. Look at the
            // primary we're about to push: 38/48 = colour, 58 =
            // underline colour.
            if (!_inSubParam && (_currentParam == 38 || _currentParam == 48 || _currentParam == 58))
            {
                PushParam();
                _inExtColorRun = true;
                return;
            }
            if (!_inSubParam)
            {
                PushParam();
                _inSubParam = true;
                _subParam = 0;
            }
            return;
        }
        if (b >= 0x20 && b <= 0x2F)
        {
            if (_inSubParam) FinalizeSubParam();
            else             PushParam();
            _inSubParam = false; _inExtColorRun = false;
            _intermediates.Append((char)b);
            _state = State.CsiIntermediate;
            return;
        }
        if (b >= 0x3C && b <= 0x3F)  { _state = State.CsiIgnore; return; } // private modifier mid-params
        if (b >= 0x40 && b <= 0x7E)
        {
            if (_inSubParam) FinalizeSubParam();
            else             PushParam();
            _inSubParam = false; _inExtColorRun = false;
            DispatchCsi((char)b);
            return;
        }
    }

    /// <summary>Stamp the pending sub-param value onto the most
    /// recently pushed primary. Called when leaving a sub-param
    /// cluster (on ';' / intermediate / final byte).</summary>
    private void FinalizeSubParam()
    {
        if (_paramCount > 0)
            _subParams[_paramCount - 1] = _subParam;
        _subParam = 0;
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
        _actions.CsiDispatchWithSub(final,
            new ReadOnlySpan<int>(_params, 0, _paramCount),
            new ReadOnlySpan<int>(_subParams, 0, _paramCount),
            _intermediates.ToString(),
            _privatePrefix);
        _state = State.Ground;
    }

    // ------------------------------------------------------------------
    // OSC: ESC ] payload terminator. Terminator is BEL (0x07) or ST (ESC \).
    // ------------------------------------------------------------------

    private void EnterOsc()
    {
        _state = State.OscString;
        _oscLen = 0;
    }

    /// <summary>Hard cap on accumulated OSC payload length (bytes).
    /// Anything past this is silently dropped until the sequence
    /// terminator arrives — prevents a runaway emitter from blowing
    /// memory.</summary>
    private const int OscMaxLength = 64 * 1024;

    private void OscString(byte b)
    {
        if (b == 0x07) { DispatchOsc(); _state = State.Ground; return; }
        if (b == 0x1B) { /* will be followed by \ for ST — we just end here for simplicity */
            DispatchOsc();
            _state = State.Escape;
            _intermediates.Clear();
            return;
        }
        // C0 controls (other than the terminators above) aren't valid
        // inside an OSC string. UTF-8 lead/continuation bytes are
        // 0xC0-0xF7 / 0x80-0xBF respectively, all >= 0x20, so they
        // pass through this filter and reach the UTF-8 decoder below.
        if (b < 0x20) return;
        if (_oscLen >= OscMaxLength) return;
        if (_oscLen >= _oscBuffer.Length)
        {
            int next = Math.Min(_oscBuffer.Length * 2, OscMaxLength);
            Array.Resize(ref _oscBuffer, next);
        }
        _oscBuffer[_oscLen++] = b;
    }

    private void DispatchOsc()
    {
        // UTF-8-decode the accumulated bytes into _oscCharBuffer.
        // Encoding.UTF8 substitutes U+FFFD for invalid sequences, so
        // mid-sequence garbage doesn't desync the dispatcher. Char
        // count is always <= byte count, so the resize check is
        // proportional but cheap.
        var enc = System.Text.Encoding.UTF8;
        int charCount = enc.GetCharCount(_oscBuffer, 0, _oscLen);
        if (charCount > _oscCharBuffer.Length)
        {
            int next = _oscCharBuffer.Length;
            while (next < charCount) next *= 2;
            _oscCharBuffer = new char[next];
        }
        int written = enc.GetChars(_oscBuffer, 0, _oscLen, _oscCharBuffer, 0);
        _actions.OscDispatch(_oscCharBuffer.AsSpan(0, written));
    }

    // ------------------------------------------------------------------
    // DCS — Device Control String. ESC P params intermediates final
    // payload ST. Parameters parse like CSI; payload accumulates until
    // the closing ST and is dispatched to the action target.
    // ------------------------------------------------------------------

    /// <summary>Hard cap on accumulated DCS payload length (chars).
    /// Anything past this is silently dropped until the sequence
    /// terminator arrives.</summary>
    private const int DcsMaxLength = 64 * 1024;

    private void EnterDcsEntry()
    {
        _state = State.DcsEntry;
        _dcsParamCount = 0;
        _dcsCurrentParam = 0;
        _dcsPrivatePrefix = (char)0;
        _dcsIntermediates.Clear();
        _dcsFinal = (char)0;
        _dcsLen = 0;
    }

    private void DcsEntry(byte b)
    {
        if (b < 0x20) return;
        if (b == 0x7F) return;
        if (b >= 0x30 && b <= 0x39) { _dcsCurrentParam = b - 0x30; _state = State.DcsParam; return; }
        if (b == 0x3B)               { PushDcsParam(); _state = State.DcsParam; return; }
        if (b >= 0x3C && b <= 0x3F)  { _dcsPrivatePrefix = (char)b; _state = State.DcsParam; return; }
        if (b >= 0x20 && b <= 0x2F)  { _dcsIntermediates.Append((char)b); _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)  { _dcsFinal = (char)b; _state = State.DcsPassthrough; return; }
    }

    private void DcsParam(byte b)
    {
        if (b < 0x20) return;
        if (b == 0x7F) return;
        if (b >= 0x30 && b <= 0x39)
        {
            if (_dcsCurrentParam < ParamMax)
                _dcsCurrentParam = _dcsCurrentParam * 10 + (b - 0x30);
            return;
        }
        if (b == 0x3B) { PushDcsParam(); return; }
        if (b >= 0x20 && b <= 0x2F)
        {
            PushDcsParam();
            _dcsIntermediates.Append((char)b);
            _state = State.DcsIntermediate;
            return;
        }
        if (b >= 0x3C && b <= 0x3F) { _state = State.DcsIgnore; return; }
        if (b >= 0x40 && b <= 0x7E)
        {
            PushDcsParam();
            _dcsFinal = (char)b;
            _state = State.DcsPassthrough;
            return;
        }
    }

    private void DcsIntermediate(byte b)
    {
        if (b < 0x20) return;
        if (b >= 0x20 && b <= 0x2F) { _dcsIntermediates.Append((char)b); return; }
        if (b >= 0x40 && b <= 0x7E)
        {
            _dcsFinal = (char)b;
            _state = State.DcsPassthrough;
            return;
        }
    }

    private void DcsPassthrough(byte b)
    {
        if (b == 0x1B)
        {
            DispatchDcs();
            _state = State.Escape;
            _intermediates.Clear();
            return;
        }
        if (b == 0x07) { DispatchDcs(); _state = State.Ground; return; }
        if (_dcsLen >= DcsMaxLength) return;
        if (_dcsLen >= _dcsBuffer.Length)
        {
            int next = Math.Min(_dcsBuffer.Length * 2, DcsMaxLength);
            Array.Resize(ref _dcsBuffer, next);
        }
        _dcsBuffer[_dcsLen++] = b;
    }

    private void DcsIgnore(byte b)
    {
        if (b == 0x1B) { _state = State.Escape; _intermediates.Clear(); return; }
    }

    private void PushDcsParam()
    {
        if (_dcsParamCount < _dcsParams.Length) _dcsParams[_dcsParamCount++] = _dcsCurrentParam;
        _dcsCurrentParam = 0;
    }

    private void DispatchDcs()
    {
        // Bare DCS with no final byte (e.g. truncated input) is ignored.
        if (_dcsFinal == 0) return;
        // Ensure at least one parameter is recorded so the action gets a
        // consistent shape regardless of whether the sender included one.
        if (_dcsParamCount == 0) _dcsParams[_dcsParamCount++] = _dcsCurrentParam;

        // UTF-8-decode the accumulated bytes into _dcsCharBuffer. Mirror
        // of the OSC dispatch path: invalid sequences substitute U+FFFD
        // so a mid-stream garbage byte doesn't desync, and char count is
        // always <= byte count so the resize check is cheap.
        var enc = System.Text.Encoding.UTF8;
        int charCount = enc.GetCharCount(_dcsBuffer, 0, _dcsLen);
        if (charCount > _dcsCharBuffer.Length)
        {
            int next = _dcsCharBuffer.Length;
            while (next < charCount) next *= 2;
            _dcsCharBuffer = new char[next];
        }
        int written = enc.GetChars(_dcsBuffer, 0, _dcsLen, _dcsCharBuffer, 0);

        _actions.DcsDispatch(
            _dcsFinal,
            new ReadOnlySpan<int>(_dcsParams, 0, _dcsParamCount),
            _dcsIntermediates.ToString(),
            _dcsPrivatePrefix,
            _dcsCharBuffer.AsSpan(0, written));
    }

    private void SosPmApcString(byte b)
    {
        if (b == 0x1B) { _state = State.Escape; _intermediates.Clear(); return; }
        if (b == 0x07) { _state = State.Ground; return; } // lenient: BEL also ends
    }
}
