# TVScreenPlus

Utilisez une TV **Google TV / Android TV** comme **écran secondaire** (étendu) d'un PC Windows, en Wi-Fi ou Ethernet, comme si elle était branchée en HDMI.

## Téléchargement (dernière version)
- PC Windows : https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip
- Google TV : https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk

Chaque push publie automatiquement une nouvelle Release `v1.0.N`.

## Installation (2 minutes)

1. **PC Windows 10/11** : téléchargez `TVScreenPlus-PC.zip` (Releases ou artefact de la CI), décompressez-le où vous voulez et lancez `TVScreenPlus.exe` (accepter la demande administrateur).
   Au premier lancement, il installe tout seul le pilote d'écran virtuel, ouvre le pare-feu et se met au démarrage de Windows. Une icône apparaît près de l'horloge.
2. **TV** : installez `TVScreenPlus-TV.apk` (via *Send Files to TV*, *Downloader* ou `adb install`), puis ouvrez l'appli.
   Les PC du réseau apparaissent automatiquement ; s'il n'y en a qu'un, la connexion est **automatique**, et la TV se reconnecte toute seule au dernier PC utilisé.

C'est tout : un nouvel écran apparaît dans Windows (Paramètres › Affichage) dès que la TV est connectée, et disparaît quand elle se déconnecte (touche *Retour*).

## Pourquoi c'est rapide

| Étape | Technique |
|---|---|
| Écran virtuel | Pilote IddCx open source ([Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), MIT) : un vrai moniteur étendu pour Windows |
| Capture | Desktop Duplication (DXGI) directement dans le GPU, curseur inclus |
| Encodage | Matériel **zéro copie** : NVIDIA NVENC, AMD AMF ou Intel Quick Sync, détecté automatiquement (repli CPU x264 ultrafast) ; HEVC si la TV le décode, sinon H.264 ; réglages ultra-faible latence (pas de B-frames, CBR, VBV court) |
| Transport | TCP sans Nagle, une trame = un paquet, aucune mise en tampon |
| Décodage TV | MediaCodec matériel en mode *low-latency* (+ clés constructeur Amlogic/MediaTek/Qualcomm), rendu immédiat sur la surface, Wi-Fi en mode *low latency*, affichage forcé en 60 Hz |

Par défaut : 1080p 60 i/s, ~17 Mb/s (HEVC) ou ~25 Mb/s (H.264). Le menu de l'icône permet de passer en 1440p / 4K / natif et de régler le débit (*Wi-Fi faible*, *Haute*, *Maximale*).
Conseil : PC en Ethernet + TV en Ethernet ou Wi-Fi 5 GHz pour une fluidité maximale.

## Dépannage
- **La TV ne trouve pas le PC** : même réseau ? Isolation Wi-Fi (réseau invité) désactivée ? Sinon saisissez l'IP du PC sur la TV.
- **Journal** : clic droit sur l'icône › *Ouvrir le journal* (`C:\ProgramData\TVScreenPlus\log.txt`).

## Compiler
- PC : `dotnet build windows/TVScreenPlus -c Release` (.NET Framework 4.8, déjà présent sur Windows ; le zip CI inclut `ffmpeg.exe` et le pilote, sinon ils sont téléchargés au premier lancement).
- TV : `cd android && ./gradlew assembleRelease`.
- La CI GitHub (`.github/workflows/build.yml`) produit les deux paquets et publie une Release à chaque push. Pour que chaque APK s'installe par-dessus le précédent, définir les secrets `ANDROID_KEYSTORE_BASE64` (keystore en base64, alias `tvsp`) et `ANDROID_KEYSTORE_PASSWORD` ; sans eux, l'APK est signé avec une clé de debug.

## Protocole
UDP 47801 : la TV diffuse `TVSP?`, le PC répond `TVSP!\nname=…`. TCP 47800 : la TV envoie `TVSP1;name=…;w=…;h=…;hevc=0|1`, le PC répond `OK;codec=…;w=…;h=…` puis des paquets `[u32 taille][u8 flags][unité d'accès Annex-B]`.
