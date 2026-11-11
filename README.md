# TVScreenPlus

**Transformez votre TV Google TV / Android TV en écran secondaire de votre PC Windows, sans câble.**

La TV devient un vrai deuxième écran (mode *étendre*) : on y glisse des fenêtres, la souris y passe, comme si elle était branchée en HDMI. L'image est encodée par la carte graphique du PC et décodée par la puce vidéo de la TV, pour une image nette et fluide avec très peu de latence.

[![Télécharger pour Windows](https://img.shields.io/badge/T%C3%A9l%C3%A9charger-PC%20Windows-0078D6?style=for-the-badge&logo=windows)](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip)
[![Télécharger pour Google TV](https://img.shields.io/badge/T%C3%A9l%C3%A9charger-Google%20TV-3DDC84?style=for-the-badge&logo=android)](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk)

---

## Sommaire
- [Points forts](#points-forts)
- [Configuration requise](#configuration-requise)
- [Installation](#installation)
- [Utilisation](#utilisation)
- [Réglage automatique de la qualité](#réglage-automatique-de-la-qualité)
- [Mises à jour](#mises-à-jour)
- [Dépannage](#dépannage)
- [Désinstallation](#désinstallation)
- [Fonctionnement technique](#fonctionnement-technique)
- [Compiler soi-même](#compiler-soi-même)
- [Crédits](#crédits)

---

## Points forts
- **Plug & play** : la TV trouve toute seule les PC du réseau et se connecte automatiquement.
- **Vrai écran étendu** : Windows voit un moniteur supplémentaire, visible dans *Paramètres › Affichage*. Il n'existe que pendant la connexion.
- **Qualité et fluidité** : jusqu'en 4K à 60 i/s. Encodage matériel NVIDIA / AMD / Intel, HEVC ou H.264, décodage matériel faible latence sur la TV.
- **Réglage automatique** : le réseau, le PC et la TV sont mesurés à chaque connexion pour choisir la meilleure résolution et le meilleur débit. Le débit s'adapte en continu si le réseau faiblit.
- **Léger** : le programme PC pèse environ 50 Ko (hors ffmpeg) et l'application TV moins de 100 Ko. Aucun runtime à installer.
- **Mises à jour automatiques** sur le PC et proposées sur la TV.

## Configuration requise
| | Minimum | Recommandé |
|---|---|---|
| **PC** | Windows 10 (2004+) ou 11, 64 bits | Carte graphique NVIDIA GTX 900+, AMD RX 400+ ou Intel 6ᵉ gén.+ |
| **TV** | Google TV / Android TV 5.0+ | Chromecast avec Google TV, Shield, TV Sony/TCL/Philips récentes |
| **Réseau** | PC et TV sur le même réseau local | PC en Ethernet et TV en Ethernet ou Wi-Fi 5 GHz / Wi-Fi 6 |

> Sans carte graphique compatible, le processeur prend le relais (qualité et fluidité réduites).

## Installation

### 1. Sur le PC Windows
1. Téléchargez **[TVScreenPlus-PC.zip](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip)** et décompressez-le n'importe où.
2. Lancez **`TVScreenPlus.exe`** et acceptez la demande d'autorisation administrateur.
   - Si Windows SmartScreen affiche « Windows a protégé votre ordinateur » : cliquez sur *Informations complémentaires*, puis *Exécuter quand même* (le programme n'est pas signé numériquement).
3. C'est installé. Le programme :
   - s'installe dans `C:\Program Files\TVScreenPlus` et remplace l'ancienne version s'il y en a une ;
   - installe le pilote d'écran virtuel et autorise le pare-feu Windows ;
   - se lance à chaque démarrage de Windows, avec une icône 📺 près de l'horloge ;
   - apparaît dans *Paramètres › Applications*.

   Vous pouvez supprimer le dossier décompressé.

### 2. Sur la TV
L'application n'est pas sur le Play Store : il faut installer le fichier APK.

**Méthode simple, avec l'appli *Downloader*** (gratuite sur le Play Store de la TV) :
1. Installez *Downloader* depuis le Play Store de la TV.
2. Autorisez-la à installer des applications : *Paramètres › Applications › Sécurité et restrictions › Sources inconnues › Downloader*.
3. Dans *Downloader*, saisissez l'adresse :
   `https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk`
4. Installez, puis ouvrez **TVScreenPlus** depuis l'accueil de la TV.

*Autres méthodes : l'appli « Send Files to TV » depuis le PC, ou `adb install TVScreenPlus-TV.apk`.*

## Utilisation
1. Le PC doit être allumé, avec TVScreenPlus lancé (icône près de l'horloge).
2. Ouvrez **TVScreenPlus** sur la TV : les PC du réseau s'affichent.
   - S'il n'y a qu'un PC, ou si c'est le dernier utilisé, la connexion est **automatique**.
   - Sinon, choisissez le PC avec la télécommande.
3. La première connexion prend quelques secondes de plus (calibrage). Le nouvel écran apparaît ensuite dans Windows.

**Télécommande pendant la diffusion**
| Touche | Action |
|---|---|
| **OK** ou **Menu** | Afficher / masquer les statistiques (résolution, débit, images/s) |
| **Retour** | Se déconnecter. L'écran disparaît de Windows |

**Menu de l'icône sur le PC** (clic droit) : résolution, qualité, démarrage avec Windows, mises à jour, recalibrage, journal.

> Astuce : pour placer la TV à gauche ou à droite de votre écran principal, faites-la glisser dans *Paramètres › Système › Affichage*.

## Réglage automatique de la qualité
À chaque connexion, en 2 à 5 secondes :
1. **Réseau** : la latence et le débit réel entre le PC et la TV sont mesurés.
2. **TV** : l'application indique la résolution de l'écran, sa fréquence (50 ou 60 Hz) et la résolution maximale que sa puce vidéo peut décoder.
3. **PC** : l'encodeur de la carte graphique est détecté puis **testé en conditions réelles** (résultat mémorisé). Le réglage « qualité » est préféré, et le réglage « rapide » sert si le PC n'est pas assez puissant.
4. TVScreenPlus choisit la **résolution la plus élevée** que l'ensemble supporte, avec le débit adapté.

Pendant la diffusion, si le réseau sature (micro-ondes, autre appareil qui télécharge…), le débit **baisse automatiquement** pour éviter les saccades, puis **remonte** quand la connexion redevient stable.

Pour forcer une valeur : clic droit sur l'icône › *Résolution* ou *Qualité / débit*. Si vous changez de carte graphique, utilisez *Recalibrer*.

## Mises à jour
- **PC** : le programme vérifie toutes les 6 heures et s'installe tout seul quand aucune TV n'est connectée. Vérification manuelle : clic droit › *Rechercher une mise à jour*.
- **TV** : au lancement, l'application propose la nouvelle version. Android demande toujours une confirmation.
- **Installation manuelle** : lancer une version plus récente du `.exe` ou de l'`.apk` remplace automatiquement l'ancienne.

Toutes les versions sont listées dans les [Releases](https://github.com/AbelSlawd/TVScreenPlus/releases).

## Dépannage
| Problème | Solution |
|---|---|
| La TV ne trouve pas le PC | Vérifiez que les deux sont sur le **même réseau** (pas de réseau « invité » ni d'isolation Wi-Fi). Sinon, saisissez l'adresse IP du PC sur la TV (`ipconfig` dans un terminal Windows). |
| Image saccadée | Préférez l'Ethernet ou le Wi-Fi 5 GHz, rapprochez la TV de la box. Les statistiques (touche OK) montrent le débit réel. |
| Image floue | Clic droit sur l'icône › *Qualité / débit › Maximale*, et vérifiez que *Résolution* est sur *Automatique* ou *Native*. |
| « Mise à jour impossible : autre signature » sur la TV | Désinstallez l'application de la TV puis réinstallez l'APK une fois. Les mises à jour suivantes fonctionneront. |
| Rien ne s'affiche après la connexion | Clic droit › *Recalibrer*, puis reconnectez la TV. Mettez à jour le pilote de la carte graphique. |
| Autre souci | Clic droit › *Ouvrir le journal* (`C:\ProgramData\TVScreenPlus\log.txt`) et joignez-le à une [issue](https://github.com/AbelSlawd/TVScreenPlus/issues). |

## Désinstallation
- **PC** : *Paramètres › Applications › TVScreenPlus › Désinstaller*. Le pilote d'écran virtuel, la règle du pare-feu et le démarrage automatique sont aussi supprimés.
- **TV** : *Paramètres › Applications › TVScreenPlus › Désinstaller*.

## Fonctionnement technique
```
PC Windows                                                   TV Google TV
┌─────────────────────────────────────────────┐   réseau    ┌──────────────────────────────┐
│ Écran virtuel (pilote IddCx)                │   local     │ Décodeur matériel MediaCodec │
│   → capture GPU (Desktop Duplication)       │  ───────▶   │   (mode faible latence)      │
│   → encodeur GPU NVENC / AMF / Quick Sync   │   TCP       │   → affichage direct (60 Hz) │
└─────────────────────────────────────────────┘             └──────────────────────────────┘
```
| Étape | Technique |
|---|---|
| Écran virtuel | [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (IddCx, MIT), activé uniquement pendant la connexion |
| Capture | DXGI Desktop Duplication (`ddagrab`), image restant dans la mémoire du GPU, curseur inclus |
| Encodage | FFmpeg avec NVENC, AMF ou Quick Sync en **zéro copie** (repli x264). HEVC si la TV le décode, sinon H.264. Qualité constante plafonnée (texte net), AQ spatiale, sans B-frames, tampon court |
| Transport | TCP sans algorithme de Nagle, une image = un paquet, tampons dimensionnés à environ 40 ms |
| Contrôle | Mesure du débit et de la latence à la connexion, débit adaptatif selon la saturation du réseau, statistiques remontées par la TV |
| Décodage TV | MediaCodec matériel en mode *low-latency* (+ options constructeur), rendu immédiat, Wi-Fi en mode faible latence, affichage calé sur la fréquence de la TV |

**Ports réseau** : UDP 47801 (découverte), TCP 47800 (flux). La règle de pare-feu est ajoutée automatiquement.

<details>
<summary>Protocole</summary>

- **Découverte** : la TV diffuse `TVSP?` en UDP et le PC répond `TVSP!\nname=…\nbusy=0|1\nver=…`.
- **Connexion** : la TV envoie `TVSP1;v=2;name=…;w=…;h=…;hz=…;hevc=<hauteur max>;avc=<hauteur max>`.
- **Flux** : paquets `[u32 taille][u8 flags][données]`. Flags : `0x80` remplissage (test de débit), `0x40` ping (la TV répond `ACK`), `0x20` configuration, `0x10` message d'état, sinon une image vidéo Annex-B (`1` = image clé).
- **Retour TV → PC** : `STAT;fps=…;kbps=…;drop=…` toutes les 2 secondes.
</details>

## Compiler soi-même
```bash
# Programme PC (.NET Framework 4.8, inclus dans Windows)
dotnet build windows/TVScreenPlus -c Release

# Application TV
cd android && ./gradlew assembleRelease
```
Sans `ffmpeg.exe` ni pilote à côté du programme, ils sont téléchargés au premier lancement.

**Intégration continue** (`.github/workflows/build.yml`) : chaque push compile les deux applications, ajoute ffmpeg et le pilote, puis publie une Release `v1.0.N`.
Pour que les mises à jour de l'APK s'installent par-dessus les précédentes, la CI signe l'APK avec une clé fixe fournie par deux secrets du dépôt : `ANDROID_KEYSTORE_BASE64` (keystore en base64, alias `tvsp`) et `ANDROID_KEYSTORE_PASSWORD`.

## Crédits
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (licence MIT) : écran virtuel Windows.
- [FFmpeg](https://ffmpeg.org/), version [BtbN](https://github.com/BtbN/FFmpeg-Builds) (licence GPL) : capture et encodage.
