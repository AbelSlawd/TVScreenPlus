# TVScreenPlus

Utilisez une TV **Google TV / Android TV** comme **écran secondaire** (étendu) d'un PC Windows, en Wi-Fi ou Ethernet, comme si elle était branchée en HDMI.

## Téléchargement (dernière version)
- PC Windows : https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip
- Google TV : https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk

Chaque push publie automatiquement une nouvelle Release `v1.0.N`.

## Installation (2 minutes)

1. **PC Windows 10/11** : décompressez `TVScreenPlus-PC.zip` n'importe où et lancez `TVScreenPlus.exe` (accepter la demande administrateur).
   Il s'installe dans `C:\Program Files\TVScreenPlus`, **arrête et supprime l'ancienne version** s'il y en a une, installe le pilote d'écran virtuel, ouvre le pare-feu, se lance au démarrage de Windows et apparaît dans *Paramètres › Applications* (désinstallation propre). Le dossier décompressé peut ensuite être supprimé.
2. **TV** : installez `TVScreenPlus-TV.apk` (via *Send Files to TV*, *Downloader* ou `adb install`), puis ouvrez l'appli.
   Les PC du réseau apparaissent automatiquement ; s'il n'y en a qu'un, la connexion est **automatique**, et la TV se reconnecte toute seule au dernier PC utilisé.

Un nouvel écran apparaît dans Windows dès que la TV est connectée, et disparaît quand elle se déconnecte (touche *Retour*). Sur la TV, **OK** ou **Menu** affiche les statistiques en direct (résolution, débit, images/s).

## Réglage automatique
À chaque connexion :
1. **Réseau** : la latence et le débit réel PC → TV sont mesurés (≈1 s).
2. **TV** : l'appli annonce la résolution de l'écran, sa fréquence (50/60 Hz) et la résolution maximale que son décodeur matériel tient à 60 i/s (HEVC et H.264).
3. **PC** : l'encodeur matériel est détecté puis **calibré en conditions réelles** (préréglage « qualité » d'abord, « rapide » sinon) ; résultat mémorisé.
4. La résolution la plus haute que réseau + PC + TV tiennent est choisie, avec le débit qui va avec. Pendant la diffusion, le débit baisse automatiquement si le réseau sature, puis remonte quand il redevient stable.

Tout reste réglable manuellement depuis l'icône (Résolution, Qualité, Recalibrer).

## Mises à jour
- **PC** : vérification toutes les 6 h (et via le menu *Rechercher une mise à jour*) ; installation automatique quand aucune TV n'est connectée.
- **TV** : l'appli vérifie au lancement et propose la mise à jour (une confirmation Android est obligatoire).

## Pourquoi c'est rapide

| Étape | Technique |
|---|---|
| Écran virtuel | Pilote IddCx open source ([Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), MIT) : un vrai moniteur étendu pour Windows |
| Capture | Desktop Duplication (DXGI) directement dans le GPU, curseur inclus |
| Encodage | Matériel **zéro copie** : NVIDIA NVENC, AMD AMF ou Intel Quick Sync, détecté automatiquement (repli CPU x264 ultrafast) ; HEVC si la TV le décode, sinon H.264 ; qualité constante plafonnée (texte net sur bureau statique), AQ spatiale, pas de B-frames, VBV court |
| Transport | TCP sans Nagle, une trame = un paquet, aucune mise en tampon |
| Décodage TV | MediaCodec matériel en mode *low-latency* (+ clés constructeur Amlogic/MediaTek/Qualcomm), rendu immédiat sur la surface, Wi-Fi en mode *low latency*, affichage forcé en 60 Hz |

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
