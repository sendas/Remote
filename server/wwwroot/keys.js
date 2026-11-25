// Tradução de KeyboardEvent.code (posição física da tecla) para Virtual-Key do Windows.
// Usa-se a posição física para o controlo remoto funcionar bem mesmo com Mac ↔ Windows
// e com o teclado português (o layout é aplicado pelo Windows do aluno).
"use strict";
window.VK = (() => {
  const m = {
    Backspace: 0x08, Tab: 0x09, Enter: 0x0D, NumpadEnter: 0x0D, ShiftLeft: 0xA0, ShiftRight: 0xA1,
    ControlLeft: 0xA2, ControlRight: 0xA3, AltLeft: 0xA4, AltRight: 0xA5, Pause: 0x13, CapsLock: 0x14,
    Escape: 0x1B, Space: 0x20, PageUp: 0x21, PageDown: 0x22, End: 0x23, Home: 0x24,
    ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28, PrintScreen: 0x2C,
    Insert: 0x2D, Delete: 0x2E, ContextMenu: 0x5D,
    // No Mac, Cmd é mapeado para Ctrl (atalhos como Cmd+C fazem Ctrl+C no Windows).
    MetaLeft: 0xA2, MetaRight: 0xA3,
    NumLock: 0x90, ScrollLock: 0x91,
    Semicolon: 0xBA, Equal: 0xBB, Comma: 0xBC, Minus: 0xBD, Period: 0xBE, Slash: 0xBF,
    Backquote: 0xC0, BracketLeft: 0xDB, Backslash: 0xDC, BracketRight: 0xDD, Quote: 0xDE,
    IntlBackslash: 0xE2,
    NumpadMultiply: 0x6A, NumpadAdd: 0x6B, NumpadSubtract: 0x6D, NumpadDecimal: 0x6E, NumpadDivide: 0x6F,
  };
  for (let i = 0; i < 26; i++) m["Key" + String.fromCharCode(65 + i)] = 0x41 + i;
  for (let i = 0; i < 10; i++) { m["Digit" + i] = 0x30 + i; m["Numpad" + i] = 0x60 + i; }
  for (let i = 1; i <= 24; i++) m["F" + i] = 0x6F + i;
  return m;
})();
