package com.droidlens.app.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.droidlens.app.Codec
import com.droidlens.app.Quality
import com.droidlens.app.Resolution
import com.droidlens.app.ui.screen.PanelPosition
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsRepository(private val context: Context) {

    companion object {
        val KEY_QUALITY        = stringPreferencesKey("quality")
        val KEY_CODEC          = stringPreferencesKey("codec")
        val KEY_RESOLUTION     = stringPreferencesKey("resolution")
        val KEY_FRONT_CAMERA   = booleanPreferencesKey("front_camera")
        val KEY_PANEL_POSITION = stringPreferencesKey("panel_position")
    }

    val settingsFlow: Flow<SavedSettings> = context.dataStore.data.map { prefs ->
        SavedSettings(
            quality = Quality.entries.find {
                it.name == prefs[KEY_QUALITY]
            } ?: Quality.MEDIUM,
            codec = Codec.entries.find {
                it.name == prefs[KEY_CODEC]
            } ?: Codec.MJPEG,
            resolution = Resolution.entries.find {
                it.name == prefs[KEY_RESOLUTION]
            } ?: Resolution.HD,
            isFrontCamera = prefs[KEY_FRONT_CAMERA] ?: false,
            panelPosition = PanelPosition.entries.find {
                it.name == prefs[KEY_PANEL_POSITION]
            } ?: PanelPosition.RIGHT
        )
    }

    suspend fun save(settings: SavedSettings) {
        context.dataStore.edit { prefs ->
            prefs[KEY_QUALITY]        = settings.quality.name
            prefs[KEY_CODEC]          = settings.codec.name
            prefs[KEY_RESOLUTION]     = settings.resolution.name
            prefs[KEY_FRONT_CAMERA]   = settings.isFrontCamera
            prefs[KEY_PANEL_POSITION] = settings.panelPosition.name
        }
    }
}

data class SavedSettings(
    val quality: Quality = Quality.MEDIUM,
    val codec: Codec = Codec.MJPEG,
    val resolution: Resolution = Resolution.HD,
    val isFrontCamera: Boolean = false,
    val panelPosition: PanelPosition = PanelPosition.RIGHT
)