namespace Wagterm.Core;

internal enum ParserState
{
    Ground,
    Escape,
    EscIntermediate,
    CsiEntry,
    CsiParam,
    CsiIntermediate,
    CsiIgnore,
    OscString,
    OscEsc,
    ApcString,
    ApcEsc,
    DcsString,
    DcsEsc,
}
