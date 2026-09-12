package com.droidlens.app.ui.theme

import androidx.compose.ui.graphics.Color

// Accent
val AccentIndigo = Color(0xFF818CF8)
val AccentAmber = Color(0xFFFBBF24)       // light theme - yellow for dark bg
val AccentAmberLight = Color(0xFFFFA700)  // dark theme — amber for light bg (note: low contrast on light bg, see conversation)

// On-accent: text/icon color placed on top of a solid accent fill (matches
// PC client's OnAccentColor in App.axaml). Same dark value in both themes —
// contrast against the amber/indigo accents holds either way.
val OnAccent = Color(0xFF12101C)

// Dark
val BgBaseDark = Color(0xFF0E0D14)
val BgPanelDark = Color(0xFF161420)
val TextPrimaryDark = Color(0xFFF1F5F9)
val TextMutedDark = Color(0xFF6B6880)
val BorderSubtleDark = Color(0x1AFFFFFF)

// Light
val BgBaseLight = Color(0xFFE0E3F7)
val BgPanelLight = Color(0xFFFFFFFF)
val TextPrimaryLight = Color(0xFF0F172A)
val TextMutedLight = Color(0xFF94A3B8)
val BorderSubtleLight = Color(0x1A000000)