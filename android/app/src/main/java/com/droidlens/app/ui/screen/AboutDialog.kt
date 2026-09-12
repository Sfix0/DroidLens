package com.droidlens.app.ui.screen

import androidx.compose.animation.core.EaseOutCubic
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupProperties
import com.droidlens.app.R
import androidx.compose.runtime.LaunchedEffect

// Shared placeholder — same repo currently hosts both the Android app and,
// eventually, the PC client. Update the PC-client URL once it has its own
private const val PROJECT_REPO_URL = "https://github.com/Sfix0/DroidLens"
private const val PC_CLIENT_URL = "https://github.com/Sfix0/DroidLens/tree/main/client"
private const val AUTHOR_NAME = "Rafik Akhmedov"
private const val AUTHOR_HANDLE = "Sfix0"

// GitHub mark (Simple Icons, 24x24) — material-icons doesn't ship brand icons,
// so we inline the path instead of adding a drawable resource.
private val GithubIcon: ImageVector by lazy {
    ImageVector.Builder(
        name = "Github",
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f
    ).addPath(
        pathData = addPathNodes(
            "M12 0.297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385 " +
                "0.6 0.113 0.82-0.258 0.82-0.577 0-0.285-0.01-1.04-0.015-2.04 " +
                "-3.338 0.724-4.042-1.61-4.042-1.61 " +
                "C4.422 18.07 3.633 17.7 3.633 17.7 " +
                "c-1.087-0.744 0.084-0.729 0.084-0.729 " +
                "1.205 0.084 1.838 1.236 1.838 1.236 " +
                "1.07 1.835 2.809 1.305 3.495 0.998 " +
                "0.108-0.776 0.417-1.305 0.76-1.605 " +
                "-2.665-0.3-5.466-1.332-5.466-5.93 " +
                "0-1.31 0.465-2.38 1.235-3.22 " +
                "-0.135-0.303-0.54-1.523 0.105-3.176 " +
                "0 0 1.005-0.322 3.3 1.23 " +
                "0.96-0.267 1.98-0.399 3-0.405 " +
                "1.02 0.006 2.04 0.138 3 0.405 " +
                "2.28-1.552 3.285-1.23 3.285-1.23 " +
                "0.645 1.653 0.24 2.873 0.12 3.176 " +
                "0.765 0.84 1.23 1.91 1.23 3.22 " +
                "0 4.61-2.805 5.625-5.475 5.92 " +
                "0.42 0.36 0.81 1.096 0.81 2.22 " +
                "0 1.606-0.015 2.896-0.015 3.286 " +
                "0 0.315 0.21 0.69 0.825 0.57 " +
                "C20.565 22.092 24 17.592 24 12.297 " +
                "c0-6.627-5.373-12-12-12"
        ),
        fill = SolidColor(androidx.compose.ui.graphics.Color.Black)
    ).build()
}

// ---------------------------------------------------------------------------
// AboutDialog — same visual language as the idle panel (rounded cards,
// primary-tinted chips, outlineVariant borders), but opens with a pseudo
// container-transform: it scales up from a point in the bottom-right corner
// (where the About trigger button sits in the floating panel) instead of
// fading in centered like a plain AlertDialog. This is a lightweight
// stand-in for a real shared-element transition — just a scale+alpha+rise
// animation with the transform origin pinned near the trigger,
// no SharedTransitionLayout involved.
// ---------------------------------------------------------------------------

@Composable
fun AboutDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current

    // Drives the open animation. Starts false, flips to true one frame after
    // composition so the initial (collapsed) state actually renders first.
    var expanded by remember { mutableStateOf(false) }
    var closing by remember { mutableStateOf(false) }

    val progress by animateFloatAsState(
        targetValue = if (expanded && !closing) 1f else 0f,
        animationSpec = tween(
            durationMillis = if (closing) 180 else 320,
            easing = EaseOutCubic
        ),
        label = "about_dialog_progress"
    )

    LaunchedEffect(Unit) { expanded = true }

    // Once the closing animation reaches 0, actually dismiss.
    LaunchedEffect(progress, closing) {
        if (closing && progress <= 0.001f) onDismiss()
    }

    fun requestClose() { closing = true }

    Popup(
        alignment = Alignment.Center,
        properties = PopupProperties(focusable = true, dismissOnBackPress = true, dismissOnClickOutside = false)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.55f * progress))
                .pointerInput(Unit) {
                    detectTapGestures(onTap = { requestClose() })
                }
        ) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    // Sits just above the floating panel, right edge aligned
                    // with the panel (12.dp) — near the About button in the
                    // panel's own top-end corner.
                    .padding(bottom = 330.dp, end = 12.dp)
                    .widthIn(max = 420.dp)
                    .fillMaxWidth(0.9f)
                    .graphicsLayer {
                        // Origin pinned to the bottom-right corner of the card —
                        // matches the About button's position below, so the card
                        // reads as expanding out of that button, rising upward.
                        transformOrigin = TransformOrigin(1f, 1f)
                        scaleX = 0.35f + 0.65f * progress
                        scaleY = 0.35f + 0.65f * progress
                        translationY = (1f - progress) * 150f
                        alpha = progress
                    }
                    // Swallow taps on the card itself so they don't fall
                    // through to the scrim's dismiss handler above.
                    .pointerInput(Unit) {
                        detectTapGestures(onTap = { /* consume */ })
                    }
            ) {
                AboutDialogCard(
                    onDismiss = { requestClose() },
                    onCopy = { url -> clipboard.setText(AnnotatedString(url)) },
                    onOpen = { url -> openUrl(context, url) }
                )
            }
        }
    }
}

@Composable
private fun AboutDialogCard(
    onDismiss: () -> Unit,
    onCopy: (String) -> Unit,
    onOpen: (String) -> Unit
) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(28.dp)),
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(28.dp),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(28.dp))
                .padding(20.dp),
        ) {
            // ── Hero row: title + version chip + close ──────────────────
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Row(
                    modifier = Modifier.weight(1f),
                    verticalAlignment = Alignment.Bottom
                ) {
                    Text(
                        text = "Droid",
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Black,
                        color = MaterialTheme.colorScheme.onSurface
                    )
                    Text(
                        text = "Lens",
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Black,
                        color = MaterialTheme.colorScheme.primary
                    )
                    Spacer(Modifier.width(9.dp))
                    VersionChip()
                }

                IconButtonSurface(
                    icon = Icons.Filled.Close,
                    contentDescription = stringResource(R.string.about_close),
                    onClick = onDismiss
                )
            }

            Spacer(Modifier.height(14.dp))
            HorizontalDivider()
            Spacer(Modifier.height(16.dp))

            // ── Developer ────────────────────────────────────────────────
            SectionLabel(text = stringResource(R.string.about_section_developer))
            Spacer(Modifier.height(8.dp))
            DeveloperCard(
                onHandleClick = {
                    onOpen("https://github.com/$AUTHOR_HANDLE")
                }
            )

            Spacer(Modifier.height(16.dp))

            // ── Project links ────────────────────────────────────────────
            SectionLabel(text = stringResource(R.string.about_section_project))
            Spacer(Modifier.height(8.dp))
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                ProjectLinkCard(
                    title = stringResource(R.string.about_repo_title),
                    url = PROJECT_REPO_URL,
                    onCopy = { onCopy(PROJECT_REPO_URL) },
                    onOpen = { onOpen(PROJECT_REPO_URL) }
                )
                ProjectLinkCard(
                    title = stringResource(R.string.about_pc_client_title),
                    url = PC_CLIENT_URL,
                    onCopy = { onCopy(PC_CLIENT_URL) },
                    onOpen = { onOpen(PC_CLIENT_URL) }
                )
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Small building blocks
// ---------------------------------------------------------------------------

@Composable
private fun VersionChip() {
    Text(
        text = stringResource(R.string.about_version),
        fontSize = 11.sp,
        fontWeight = FontWeight.Normal,
        color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f)
    )
}

@Composable
private fun SectionLabel(text: String) {
    Text(
        text = text,
        fontSize = 11.sp,
        fontWeight = FontWeight.SemiBold,
        letterSpacing = 0.4.sp,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(start = 4.dp)
    )
}

@Composable
private fun IconButtonSurface(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    tinted: Boolean = false
) {
    val bg = if (tinted) {
        MaterialTheme.colorScheme.primary.copy(alpha = 0.1f)
    } else {
        MaterialTheme.colorScheme.onSurface.copy(alpha = 0.06f)
    }
    val tint = if (tinted) {
        MaterialTheme.colorScheme.primary
    } else {
        MaterialTheme.colorScheme.onSurfaceVariant
    }

    Box(
        modifier = Modifier
            .size(32.dp)
            .clip(CircleShape)
            .background(bg)
            .clickable { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Icon(
            imageVector = icon,
            contentDescription = contentDescription,
            tint = tint,
            modifier = Modifier.size(15.dp)
        )
    }
}

@Composable
private fun HorizontalDivider() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(MaterialTheme.colorScheme.outlineVariant)
    )
}

@Composable
private fun DeveloperCard(onHandleClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f))
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(34.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.06f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = Icons.Filled.Person,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(18.dp)
            )
        }

        Spacer(Modifier.width(10.dp))

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = AUTHOR_NAME,
                fontSize = 13.5.sp,
                fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurface
            )
            Text(
                text = stringResource(R.string.about_developer_role),
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Row(
            modifier = Modifier
                .clip(RoundedCornerShape(12.dp))
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.06f))
                .clickable { onHandleClick() }
                .padding(horizontal = 11.dp, vertical = 7.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Text(
                text = AUTHOR_HANDLE,
                fontSize = 12.sp,
                fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun ProjectLinkCard(
    title: String,
    url: String,
    onCopy: () -> Unit,
    onOpen: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f))
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        // Leading GitHub mark — muted, matches the card tone.
        Box(
            modifier = Modifier
                .size(34.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.06f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = GithubIcon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(17.dp)
            )
        }

        Spacer(Modifier.width(10.dp))

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurface
            )
            Text(
                text = url.removePrefix("https://"),
                fontSize = 11.5.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
            )
        }

        Spacer(Modifier.width(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            IconButtonSurface(
                icon = Icons.Filled.ContentCopy,
                contentDescription = stringResource(R.string.about_link_copy_desc),
                onClick = onCopy
            )
            IconButtonSurface(
                icon = Icons.Filled.OpenInNew,
                contentDescription = stringResource(R.string.about_link_open_desc),
                onClick = onOpen,
                tinted = true
            )
        }
    }
}

private fun openUrl(context: android.content.Context, url: String) {
    try {
        val intent = android.content.Intent(
            android.content.Intent.ACTION_VIEW,
            android.net.Uri.parse(url)
        )
        context.startActivity(intent)
    } catch (e: Exception) {
        // Best-effort: no browser available or similar — ignore.
    }
}