package com.tvscreenplus

import java.util.Locale

/** Tiny translation table. Language: "system" or one of [CODES]. */
object L {
    val CODES = listOf("fr", "en", "es", "de")
    val NAMES = listOf("Français", "English", "Español", "Deutsch")
    var choice = "system"
    val lang: String
        get() = if (choice != "system") choice else Locale.getDefault().language.takeIf { it in CODES } ?: "en"

    fun t(key: String, vararg args: Any): String {
        val row = S[key] ?: return key
        val s = row.getOrNull(CODES.indexOf(lang)) ?: row[1]
        return if (args.isEmpty()) s else String.format(s, *args)
    }

    // key to (fr, en, es, de)
    private val S = mapOf(
        "searching" to arrayOf("Recherche des ordinateurs sur le réseau…", "Looking for computers on the network…", "Buscando ordenadores en la red…", "Suche nach Computern im Netzwerk…"),
        "choose" to arrayOf("Choisissez un ordinateur :", "Choose a computer:", "Elija un ordenador:", "Wählen Sie einen Computer:"),
        "connecting" to arrayOf("Connexion à %s…", "Connecting to %s…", "Conectando a %s…", "Verbinde mit %s…"),
        "disconnected" to arrayOf("Déconnecté", "Disconnected", "Desconectado", "Getrennt"),
        "disconnected_err" to arrayOf("Déconnecté : %s", "Disconnected: %s", "Desconectado: %s", "Getrennt: %s"),
        "disconnected_back" to arrayOf("Déconnecté – choisissez un ordinateur pour reprendre", "Disconnected – choose a computer to resume", "Desconectado – elija un ordenador para continuar", "Getrennt – wählen Sie einen Computer, um fortzufahren"),
        "busy" to arrayOf("occupé", "busy", "ocupado", "belegt"),
        "ip_hint" to arrayOf("IP du PC (si non détecté)", "PC IP (if not detected)", "IP del PC (si no se detecta)", "PC-IP (falls nicht erkannt)"),
        "connect" to arrayOf("Connecter", "Connect", "Conectar", "Verbinden"),
        "help" to arrayOf("Installez TVScreenPlus sur le PC Windows (même réseau). Retour = déconnecter, OK = statistiques.", "Install TVScreenPlus on the Windows PC (same network). Back = disconnect, OK = statistics.", "Instale TVScreenPlus en el PC Windows (misma red). Atrás = desconectar, OK = estadísticas.", "Installieren Sie TVScreenPlus auf dem Windows-PC (gleiches Netzwerk). Zurück = trennen, OK = Statistiken."),
        "version" to arrayOf("Version %s", "Version %s", "Versión %s", "Version %s"),
        "settings" to arrayOf("Paramètres", "Settings", "Ajustes", "Einstellungen"),
        "back" to arrayOf("Retour", "Back", "Volver", "Zurück"),
        "update_to" to arrayOf("Mettre à jour vers la version %s", "Update to version %s", "Actualizar a la versión %s", "Auf Version %s aktualisieren"),
        "dl_update" to arrayOf("Téléchargement de la mise à jour… %d %%", "Downloading update… %d %%", "Descargando actualización… %d %%", "Update wird heruntergeladen… %d %%"),
        "update_failed" to arrayOf("Échec de la mise à jour : %s", "Update failed: %s", "Error de actualización: %s", "Update fehlgeschlagen: %s"),
        "net_error" to arrayOf("Erreur réseau : %s", "Network error: %s", "Error de red: %s", "Netzwerkfehler: %s"),
        "stats" to arrayOf("Reçu : %.1f Mb/s · Affiché : %d i/s · Pertes : %d", "Received: %.1f Mb/s · Shown: %d fps · Dropped: %d", "Recibido: %.1f Mb/s · Mostrado: %d fps · Perdidos: %d", "Empfangen: %.1f Mb/s · Angezeigt: %d fps · Verworfen: %d"),
        "lang" to arrayOf("Langue", "Language", "Idioma", "Sprache"),
        "lang_system" to arrayOf("Langue du système", "System language", "Idioma del sistema", "Systemsprache"),
        "priority" to arrayOf("Priorité de l'image", "Picture priority", "Prioridad de imagen", "Bildpriorität"),
        "prio_auto" to arrayOf("Automatique (recommandé)", "Automatic (recommended)", "Automática (recomendado)", "Automatisch (empfohlen)"),
        "prio_smooth" to arrayOf("Fluidité", "Smoothness", "Fluidez", "Flüssigkeit"),
        "prio_quality" to arrayOf("Qualité maximale", "Maximum quality", "Calidad máxima", "Maximale Qualität"),
        "maxres" to arrayOf("Résolution maximale", "Maximum resolution", "Resolución máxima", "Maximale Auflösung"),
        "auto" to arrayOf("Automatique", "Automatic", "Automática", "Automatisch"),
        "autoconnect" to arrayOf("Connexion automatique", "Automatic connection", "Conexión automática", "Automatische Verbindung"),
        "showstats" to arrayOf("Statistiques pendant la diffusion", "Statistics while streaming", "Estadísticas durante la transmisión", "Statistiken während der Übertragung"),
        "on" to arrayOf("Activé", "On", "Activado", "Ein"),
        "off" to arrayOf("Désactivé", "Off", "Desactivado", "Aus"),
        "hwtest" to arrayOf("Test du matériel de la TV", "TV hardware test", "Prueba del hardware del TV", "TV-Hardwaretest"),
        "hwtest_run" to arrayOf("Lancer le test du matériel", "Run hardware test", "Iniciar prueba de hardware", "Hardwaretest starten"),
        "hwtest_running" to arrayOf("Test en cours : %s…", "Testing: %s…", "Probando: %s…", "Test läuft: %s…"),
        "hwtest_never" to arrayOf("Jamais testé : l'ordinateur utilise les capacités annoncées par la TV.", "Never tested: the computer uses the capabilities reported by the TV.", "Nunca probado: el ordenador usa las capacidades declaradas por el TV.", "Nie getestet: Der Computer nutzt die vom TV gemeldeten Fähigkeiten."),
        "hwtest_result" to arrayOf("HEVC : %s · H.264 : %s · Latence de décodage : %s ms\nCes résultats sont envoyés à l'ordinateur à chaque connexion.", "HEVC: %s · H.264: %s · Decode latency: %s ms\nThese results are sent to the computer on every connection.", "HEVC: %s · H.264: %s · Latencia de decodificación: %s ms\nEstos resultados se envían al ordenador en cada conexión.", "HEVC: %s · H.264: %s · Decodierlatenz: %s ms\nDiese Ergebnisse werden bei jeder Verbindung an den Computer gesendet."),
        "hwtest_done" to arrayOf("Test terminé", "Test complete", "Prueba terminada", "Test abgeschlossen"),
        "none" to arrayOf("non pris en charge", "not supported", "no compatible", "nicht unterstützt"),
        "check_updates" to arrayOf("Rechercher les mises à jour", "Check for updates", "Buscar actualizaciones", "Nach Updates suchen"),
        "checking" to arrayOf("Recherche…", "Checking…", "Buscando…", "Suche…"),
        "uptodate" to arrayOf("Vous avez la dernière version (%s).", "You have the latest version (%s).", "Tiene la última versión (%s).", "Sie haben die neueste Version (%s)."),
        "update_check_failed" to arrayOf("Impossible de vérifier les mises à jour.", "Unable to check for updates.", "No se pueden buscar actualizaciones.", "Updates können nicht geprüft werden."),
        "forget" to arrayOf("Oublier les ordinateurs mémorisés", "Forget saved computers", "Olvidar ordenadores guardados", "Gespeicherte Computer vergessen"),
        "forgotten" to arrayOf("Ordinateurs oubliés.", "Computers forgotten.", "Ordenadores olvidados.", "Computer vergessen."),
        "about" to arrayOf("À propos", "About", "Acerca de", "Über"),
        "about_text" to arrayOf("TVScreenPlus %s · %s · Android %s · IP %s · %s", "TVScreenPlus %s · %s · Android %s · IP %s · %s", "TVScreenPlus %s · %s · Android %s · IP %s · %s", "TVScreenPlus %s · %s · Android %s · IP %s · %s"),
        "ethernet" to arrayOf("Ethernet", "Ethernet", "Ethernet", "Ethernet"),
        "wifi" to arrayOf("Wi-Fi %s GHz, %d Mb/s", "Wi-Fi %s GHz, %d Mb/s", "Wi-Fi %s GHz, %d Mb/s", "WLAN %s GHz, %d Mb/s"),
        "confirm_install" to arrayOf("Confirmez l'installation de la mise à jour…", "Confirm the update installation…", "Confirme la instalación de la actualización…", "Bestätigen Sie die Installation des Updates…"),
        "installed" to arrayOf("Mise à jour installée", "Update installed", "Actualización instalada", "Update installiert"),
        "sig_conflict" to arrayOf("Mise à jour impossible : la version installée a une autre signature. Désinstallez TVScreenPlus puis installez le nouvel APK une fois.", "Update impossible: the installed version has a different signature. Uninstall TVScreenPlus, then install the new APK once.", "Actualización imposible: la versión instalada tiene otra firma. Desinstale TVScreenPlus e instale el nuevo APK una vez.", "Update nicht möglich: Die installierte Version hat eine andere Signatur. Deinstallieren Sie TVScreenPlus und installieren Sie das neue APK einmal."),
        "cancelled" to arrayOf("Mise à jour annulée", "Update cancelled", "Actualización cancelada", "Update abgebrochen"),
        "bad_reply" to arrayOf("Réponse invalide du PC", "Invalid reply from the PC", "Respuesta no válida del PC", "Ungültige Antwort vom PC"),
        "corrupt" to arrayOf("Flux corrompu", "Corrupted stream", "Flujo dañado", "Beschädigter Datenstrom"),
        "no_decoder" to arrayOf("Décodeur vidéo indisponible", "Video decoder unavailable", "Decodificador de vídeo no disponible", "Videodecoder nicht verfügbar"),
        "target" to arrayOf("cible", "target", "objetivo", "Ziel"),
        "audio_pads" to arrayOf("Son et manettes", "Sound and gamepads", "Sonido y mandos", "Ton und Controller"),
        "audio" to arrayOf("Son de l'ordinateur sur la TV", "Computer sound on the TV", "Sonido del ordenador en el TV", "Computerton auf dem TV"),
        "pads" to arrayOf("Manettes de la TV utilisées par l'ordinateur", "TV gamepads used by the computer", "Mandos del TV usados por el ordenador", "TV-Controller vom Computer verwendet"),
        "pads_note" to arrayOf("Les manettes Bluetooth ou USB connectées à la TV apparaissent sur l'ordinateur comme des manettes Xbox (4 au maximum), vibrations comprises. La télécommande garde ses touches : Retour = déconnecter.", "Bluetooth or USB gamepads connected to the TV appear on the computer as Xbox controllers (up to 4), rumble included. The remote keeps its keys: Back = disconnect.", "Los mandos Bluetooth o USB conectados al TV aparecen en el ordenador como mandos Xbox (4 como máximo), vibración incluida. El mando a distancia conserva sus teclas: Atrás = desconectar.", "Bluetooth- oder USB-Controller am TV erscheinen auf dem Computer als Xbox-Controller (maximal 4), inklusive Vibration. Die Fernbedienung behält ihre Tasten: Zurück = trennen."),
        "stats_audio" to arrayOf("Son : tampon %d ms", "Sound: %d ms buffer", "Sonido: búfer de %d ms", "Ton: Puffer %d ms"),
        "stats_pads" to arrayOf("Manettes : %d", "Gamepads: %d", "Mandos: %d", "Controller: %d"),
    )
}
