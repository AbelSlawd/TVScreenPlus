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
        "help" to arrayOf("Installez TVScreenPlus sur le PC Windows (même réseau). Pendant la diffusion : Retour = déconnecter, OK = statistiques, ▲ ▼ = mode fluide / jeu, ◀ ▶ = synchro du son.", "Install TVScreenPlus on the Windows PC (same network). While streaming: Back = disconnect, OK = statistics, ▲ ▼ = fluid / game mode, ◀ ▶ = sound sync.", "Instale TVScreenPlus en el PC Windows (misma red). Durante la transmisión: Atrás = desconectar, OK = estadísticas, ▲ ▼ = modo fluido / juego, ◀ ▶ = sincronía del sonido.", "Installieren Sie TVScreenPlus auf dem Windows-PC (gleiches Netzwerk). Während der Übertragung: Zurück = trennen, OK = Statistiken, ▲ ▼ = flüssig / Spielmodus, ◀ ▶ = Tonsynchronisation."),
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
        "forgotten" to arrayOf("Ordinateurs oubliés : chaque PC demandera d'accepter à nouveau cette TV.", "Computers forgotten: each PC will ask to accept this TV again.", "Ordenadores olvidados: cada PC pedirá aceptar de nuevo esta TV.", "Computer vergessen: jeder PC fragt erneut, ob dieser Fernseher zugelassen wird."),
        "pair_code" to arrayOf("Nouvelle TV pour ce PC : acceptez-la sur le PC si le code affiché est %s", "New TV for this PC: accept it on the PC if the code shown is %s", "TV nueva para este PC: acéptela en el PC si el código mostrado es %s", "Neuer Fernseher für diesen PC: am PC zulassen, wenn der Code %s lautet"),
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
        "sound_out" to arrayOf("Sortie du son", "Sound output", "Salida de sonido", "Tonausgabe"),
        "snd_fast" to arrayOf("Rapide", "Fast", "Rápida", "Schnell"),
        "snd_stable" to arrayOf("Stable", "Steady", "Estable", "Stabil"),
        "snd_quality" to arrayOf("Qualité TV", "TV quality", "Calidad TV", "TV-Qualität"),
        "sound_note" to arrayOf(
            "Rapide : latence minimale, sans les effets sonores de la TV. Stable : pareil, avec plus de marge pour éviter les micro-coupures (~30 ms de plus). Qualité TV : passe par les effets sonores de la TV (Dolby, égaliseur…), plus de latence ; réglez la synchro avec ◀ ▶ si besoin (mémorisée pour ce choix). Appliqué à la prochaine connexion.",
            "Fast: lowest latency, without the TV's sound effects. Steady: the same with more margin against tiny cuts (~30 ms more). TV quality: goes through the TV's sound effects (Dolby, equaliser…), more latency; adjust the sync with ◀ ▶ if needed (remembered for this choice). Applied at the next connection.",
            "Rápida: latencia mínima, sin los efectos de sonido de la TV. Estable: igual, con más margen contra micro cortes (~30 ms más). Calidad TV: pasa por los efectos de sonido de la TV (Dolby, ecualizador…), más latencia; ajuste la sincronía con ◀ ▶ si hace falta (se recuerda para esta opción). Se aplica en la próxima conexión.",
            "Schnell: geringste Latenz, ohne die Klangeffekte des Fernsehers. Stabil: ebenso, mit mehr Reserve gegen kurze Aussetzer (~30 ms mehr). TV-Qualität: über die Klangeffekte des Fernsehers (Dolby, Equalizer…), mehr Latenz; Synchronisation bei Bedarf mit ◀ ▶ anpassen (für diese Wahl gespeichert). Gilt ab der nächsten Verbindung."),
        "audio" to arrayOf("Son de l'ordinateur sur la TV", "Computer sound on the TV", "Sonido del ordenador en el TV", "Computerton auf dem TV"),
        "pads" to arrayOf("Manettes de la TV utilisées par l'ordinateur", "TV gamepads used by the computer", "Mandos del TV usados por el ordenador", "TV-Controller vom Computer verwendet"),
        "pads_note" to arrayOf("Les manettes Bluetooth ou USB connectées à la TV apparaissent sur l'ordinateur comme des manettes Xbox (4 au maximum), vibrations comprises. La télécommande garde ses touches : Retour = déconnecter.", "Bluetooth or USB gamepads connected to the TV appear on the computer as Xbox controllers (up to 4), rumble included. The remote keeps its keys: Back = disconnect.", "Los mandos Bluetooth o USB conectados al TV aparecen en el ordenador como mandos Xbox (4 como máximo), vibración incluida. El mando a distancia conserva sus teclas: Atrás = desconectar.", "Bluetooth- oder USB-Controller am TV erscheinen auf dem Computer als Xbox-Controller (maximal 4), inklusive Vibration. Die Fernbedienung behält ihre Tasten: Zurück = trennen."),
        "stats_sync" to arrayOf("Synchro : sortie du son %d ms · image retardée de %d ms · son retardé de %d ms · réglage %+d ms", "Sync: sound output %d ms · image delayed %d ms · sound delayed %d ms · adjustment %+d ms", "Sincronía: salida de sonido %d ms · imagen retrasada %d ms · sonido retrasado %d ms · ajuste %+d ms", "Synchronität: Tonausgabe %d ms · Bild verzögert %d ms · Ton verzögert %d ms · Korrektur %+d ms"),
        "av_offset" to arrayOf("Synchronisation du son :  %+d ms  (OK = remettre à 0)", "Sound sync:  %+d ms  (OK = reset)", "Sincronización del sonido:  %+d ms  (OK = restablecer)", "Tonsynchronisation:  %+d ms  (OK = zurücksetzen)"),
        "av_offset_note" to arrayOf("Le son est calé automatiquement sur l'image. Pendant la diffusion, les touches ◀ ▶ de la télécommande avancent ou retardent le son de 10 ms pour un réglage à l'oreille.", "Sound is synced to the image automatically. While streaming, the remote's ◀ ▶ keys move the sound 10 ms earlier or later to fine-tune by ear.", "El sonido se sincroniza automáticamente con la imagen. Durante la transmisión, las teclas ◀ ▶ del mando adelantan o retrasan el sonido 10 ms para ajustarlo de oído.", "Der Ton wird automatisch mit dem Bild synchronisiert. Während der Übertragung verschieben die Tasten ◀ ▶ der Fernbedienung den Ton um 10 ms früher oder später."),
        "mode_fluid" to arrayOf("Mode fluide : 60 images/s régulières, son synchronisé  (▼ mode jeu)", "Fluid mode: steady 60 fps, sound in sync  (▼ game mode)", "Modo fluido: 60 fps estables, sonido sincronizado  (▼ modo juego)", "Flüssiger Modus: stabile 60 fps, Ton synchron  (▼ Spielmodus)"),
        "mode_game" to arrayOf("Mode jeu : latence minimale  (▲ mode fluide)", "Game mode: minimum latency  (▲ fluid mode)", "Modo juego: latencia mínima  (▲ modo fluido)", "Spielmodus: minimale Latenz  (▲ flüssiger Modus)"),
        "mode_unsupported" to arrayOf("Changement de mode : mettez à jour TVScreenPlus sur le PC", "Mode switch: update TVScreenPlus on the PC", "Cambio de modo: actualice TVScreenPlus en el PC", "Moduswechsel: TVScreenPlus auf dem PC aktualisieren"),
        "av_offset_now" to arrayOf("Son %+d ms  (◀ plus tôt · ▶ plus tard)", "Sound %+d ms  (◀ earlier · ▶ later)", "Sonido %+d ms  (◀ antes · ▶ después)", "Ton %+d ms  (◀ früher · ▶ später)"),
        "stats_audio" to arrayOf("Son : tampon %d ms", "Sound: %d ms buffer", "Sonido: búfer de %d ms", "Ton: Puffer %d ms"),
        "stats_pads" to arrayOf("Manettes : %d", "Gamepads: %d", "Mandos: %d", "Controller: %d"),
    )
}
