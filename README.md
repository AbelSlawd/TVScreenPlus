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
- **Le son du PC sur la TV** : ce que joue Windows sort par les haut-parleurs de la TV, avec très peu de retard et calé sur l'image.
- **Manettes sur la TV = manettes du PC** : les manettes Bluetooth ou USB connectées à la TV sont vues par Windows comme des manettes Xbox (jusqu'à 4), vibrations comprises. Les jeux PC les reconnaissent directement.
- **Léger** : le programme PC pèse environ 100 Ko (hors ffmpeg) et l'application TV moins de 150 Ko. Aucun runtime à installer.
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
| **OK** ou **Menu** | Afficher / masquer les statistiques (résolution, débit, images/s, son, manettes) |
| **Retour** | Se déconnecter. L'écran disparaît de Windows |
| **▲ / ▼** | Mode **fluide** (60 i/s réguliers, son synchronisé) / mode **jeu** (latence minimale). Appliqué en une fraction de seconde, sans déconnexion |
| **◀ / ▶** | Avancer / retarder le son de 10 ms (réglage de la synchronisation à l'oreille, mémorisé) |

Les touches des **manettes** ne pilotent pas la TV pendant la diffusion : elles vont au PC (le bouton B ne déconnecte pas). La télécommande garde toujours ses touches.

**Menu de l'icône sur le PC** (clic droit) : *Redémarrer la connexion*, résolution, qualité, **mode jeu**, **son sur la TV**, **couper le son du PC pendant la diffusion**, **manettes de la TV**, démarrage avec Windows, mises à jour, recalibrage, **mode debug**, journal.

### Mode fluide ou mode jeu (PC)
Par défaut, TVScreenPlus est en **mode fluide** : l'encodeur garde une ou deux images d'avance pour tenir **60 images/s régulières**, ce qui ajoute au plus une trentaine de millisecondes de latence. Idéal pour le bureau, la navigation et la vidéo.

Touche **▼** de la télécommande pendant la diffusion, ou clic droit sur l'icône › *Mode jeu (latence minimale)* : chaque image est envoyée dès qu'elle est encodée et l'image n'est jamais retardée pour attendre le son. La latence est la plus faible possible, mais quelques images peuvent être sautées (environ 55 à 58 i/s en 4K). Utile pour jouer. **▲** revient au mode fluide. Le changement s'applique en une fraction de seconde, sans déconnexion, et le menu du PC suit.

### Son du PC sur la TV
Activé par défaut. Pendant la diffusion, tout ce que joue Windows (jeux, vidéos, musique, notifications) est envoyé à la TV et sort par ses haut-parleurs (ou sa barre de son).
- Le son est capturé sur la **sortie audio par défaut de Windows** : si vous changez de sortie (casque…), la TV suit automatiquement.
- Le PC continue aussi de jouer le son sur ses propres haut-parleurs. Pour ne l'entendre que sur la TV : clic droit sur l'icône › *Couper le son du PC pendant la diffusion* (le son du PC est rétabli à la déconnexion).
- Pour désactiver le son sur la TV : clic droit › *Son sur la TV*, ou dans les paramètres de la TV.
- Le son voyage sur son propre canal (UDP, environ 1,5 Mb/s, pris en compte dans le réglage du débit) : il n'attend jamais derrière une grosse image. La TV garde un tampon d'environ 30 ms, agrandi automatiquement si le réseau est irrégulier, puis réduit lentement quand il redevient stable (sans descendre sous 25 ms, 15 ms en mode jeu, pour éviter les coupures). En mode jeu, le son passe aussi par le canal « jeu » d'Android, que certaines TV traitent avec moins d'effets, donc plus vite.
- L'application demande à la TV son mode « jeu » (faible latence, ALLM) quand elle le prend en charge : moins de traitements, donc moins de retard de l'image et du son.
- **Synchronisation automatique** : la TV mesure le délai réel du son (capture → haut-parleurs ou barre de son) et celui de l'image (capture → écran), et les compare. Si le son arrive après l'image, l'image est légèrement retardée ; s'il arrive avant, c'est le son. Les changements sont progressifs et invisibles. En **mode jeu** (PC), l'image n'est jamais retardée pour attendre le son : la réactivité passe en premier.
- **Réglage à l'oreille** : pendant la diffusion, les touches **◀ ▶** de la télécommande avancent ou retardent le son de 10 ms (mémorisé). *Paramètres de la TV › Synchronisation du son* le remet à 0. Pratique avec une vidéo de test de synchronisation labiale.

### Manettes
Activées par défaut. Connectez vos manettes à la TV (*Paramètres de la TV › Télécommandes et accessoires › Ajouter un accessoire* pour le Bluetooth), puis connectez la TV au PC : chaque manette apparaît dans Windows comme une **manette Xbox 360** (jusqu'à 4). Les jeux et Steam la reconnaissent comme une manette branchée sur le PC.
- Toute manette reconnue par la TV comme manette de jeu fonctionne : Xbox, PlayStation (DualShock 4, DualSense), 8BitDo, Stadia, manettes Bluetooth « Android » génériques, manettes USB…
- Sticks, gâchettes analogiques, croix directionnelle, tous les boutons (le bouton central Xbox / PS devient le bouton *Guide*, souvent réservé par le système de la TV).
- **Vibrations** : quand un jeu fait vibrer la manette Xbox virtuelle, la manette de la TV vibre (si elle et la TV le permettent ; meilleure prise en charge à partir d'Android 12).
- Une manette connectée ou déconnectée pendant la diffusion apparaît ou disparaît aussitôt sur le PC.
- **Première utilisation** : le PC installe automatiquement (et silencieusement) le pilote **ViGEmBus**, qui crée les manettes virtuelles. Cela prend quelques secondes, une seule fois.
- Pour désactiver : clic droit sur l'icône › *Manettes de la TV*, ou dans les paramètres de la TV.

### Paramètres de la TV (⚙ en haut à droite, ou touche Menu)
| Paramètre | Rôle |
|---|---|
| **Langue** | Langue du système, ou Français / English / Español / Deutsch |
| **Priorité de l'image** | *Automatique*, *Fluidité* (débit prudent, 1080p max) ou *Qualité maximale* (débit plus élevé) |
| **Résolution maximale** | Automatique, 720p, 1080p, 1440p ou 4K |
| **Connexion automatique** | Se connecter seule au dernier PC utilisé |
| **Statistiques pendant la diffusion** | Les afficher d'office (sinon : touche OK) |
| **Son de l'ordinateur sur la TV** | Jouer le son du PC sur la TV (activé par défaut) |
| **Manettes de la TV utilisées par l'ordinateur** | Envoyer les manettes de la TV au PC (activé par défaut). Désactivé, elles pilotent la TV comme avant |
| **Test du matériel de la TV** | Mesure ce que la puce vidéo décode vraiment (voir ci-dessous) |
| **Rechercher les mises à jour** | Vérifie et installe la dernière version |
| **Oublier les ordinateurs mémorisés** | Efface le dernier PC et l'IP saisie |
| **À propos** | Version, modèle, Android, adresse IP, type de réseau (Ethernet / Wi-Fi 2,4-5-6 GHz) |

La version de l'application est affichée en haut de l'écran d'accueil.

### Curseur de la souris
Le curseur n'est plus incrusté dans la vidéo : le PC envoie sa position (plusieurs centaines de fois par seconde) et sa forme, et la TV le dessine elle-même par-dessus l'image. Il suit la souris presque instantanément, sans attendre l'encodage et le décodage de la vidéo. Avec une ancienne application TV, le curseur reste dans la vidéo.

### Mode debug (PC)
Clic droit sur l'icône › *Mode debug* : une fenêtre affiche en direct
- la **stabilité du signal** (score sur 100, saturation du réseau, gigue de latence, images perdues par la TV, baisses de débit) ;
- les **images par seconde** envoyées par le PC et affichées par la TV ;
- la **résolution**, le codec, l'encodeur et le débit ;
- la **latence** : aller-retour réseau mesuré chaque seconde, latence de décodage **mesurée en direct sur la TV** (moyenne et maximum) et estimation de la latence totale PC → écran ;
- la **saturation du décodeur de la TV** : part du temps où la puce vidéo ne suit plus le flux ;
- la **latence mesurée capture → écran** : chaque image porte son heure de capture, et la TV note quand elle s'affiche (horloges du PC et de la TV recalées en continu grâce aux pings). Le traitement d'image propre à la TV n'est pas inclus : mettez la TV en mode d'image « Jeu » pour le réduire ;
- le **son** : format capturé, débit, tampon de la TV, coupures, paquets jetés pour rattraper le retard, gigue ;
- les **manettes** connectées sur la TV et leur manette Xbox virtuelle.

Les mêmes chiffres sont écrits dans le journal toutes les 10 secondes (lignes `Stats:`), avec les informations envoyées par la TV à chaque connexion (lignes `TV:` : modèle, décodeur utilisé, détail du test du matériel). On peut ainsi analyser une session après coup, sans outil de développement sur la TV.

> Astuce : pour placer la TV à gauche ou à droite de votre écran principal, faites-la glisser dans *Paramètres › Système › Affichage*.

## Réglage automatique de la qualité
À chaque connexion, en 2 à 5 secondes :
1. **Réseau** : la latence et le débit réel entre le PC et la TV sont mesurés.
2. **TV** : l'application indique la résolution de l'écran, sa fréquence (50 ou 60 Hz), le type de réseau et la résolution maximale que sa puce vidéo décode à 60 i/s. Après le **test du matériel** (Paramètres de la TV), ce sont les valeurs **mesurées** qui sont envoyées, avec la latence de décodage. Le test encode un court clip sur la TV puis le décode en HEVC et en H.264, de la 4K à la 720p.
3. **PC** : l'encodeur de la carte graphique est détecté puis **testé en conditions réelles** (résultat mémorisé). Le réglage « qualité » est préféré, et le réglage « rapide » sert si le PC n'est pas assez puissant.
4. TVScreenPlus choisit la **résolution la plus élevée** que l'ensemble supporte, avec le débit adapté.

Pendant la diffusion, si le réseau sature (micro-ondes, autre appareil qui télécharge…), le débit **baisse automatiquement** pour éviter les saccades, puis **remonte** quand la connexion redevient stable.

Pour forcer une valeur : clic droit sur l'icône › *Résolution* ou *Qualité / débit*. Même forcé, le débit ne dépasse jamais ce que le réseau mesuré peut transporter : 85 % sur un réseau rapide, 60 % sur un lien lent comme le port Ethernet 100 Mb/s de beaucoup de TV (les pics d'images saturaient le câble : saccades, image figée, son haché). Si vous changez de carte graphique, utilisez *Recalibrer*.

## Mises à jour
- **PC** : le programme vérifie toutes les 6 heures et s'installe tout seul quand aucune TV n'est connectée. Vérification manuelle : clic droit › *Rechercher une mise à jour*.
- **TV** : au lancement, l'application propose la nouvelle version. Android demande toujours une confirmation.
- **Installation manuelle** : lancer une version plus récente du `.exe` ou de l'`.apk` remplace automatiquement l'ancienne.

Toutes les versions sont listées dans les [Releases](https://github.com/AbelSlawd/TVScreenPlus/releases).

## Dépannage
| Problème | Solution |
|---|---|
| La TV ne trouve pas le PC | Vérifiez que les deux sont sur le **même réseau** (pas de réseau « invité » ni d'isolation Wi-Fi). Sinon, saisissez l'adresse IP du PC sur la TV (`ipconfig` dans un terminal Windows). |
| Image figée, écran noir ou connexion bloquée | Clic droit sur l'icône › *Redémarrer la connexion* : la TV se reconnecte automatiquement en quelques secondes. |
| Image saccadée | Préférez l'Ethernet ou le Wi-Fi 5 GHz, rapprochez la TV de la box. Les statistiques (touche OK) montrent le débit réel. |
| Image floue | Clic droit sur l'icône › *Qualité / débit › Maximale*, et vérifiez que *Résolution* est sur *Automatique* ou *Native*. |
| « Mise à jour impossible : autre signature » sur la TV | Désinstallez l'application de la TV puis réinstallez l'APK une fois. Les mises à jour suivantes fonctionneront. |
| Rien ne s'affiche après la connexion | Clic droit › *Recalibrer*, puis reconnectez la TV. Mettez à jour le pilote de la carte graphique. |
| Pas de son sur la TV | Vérifiez *Son sur la TV* (icône du PC) et le réglage de la TV, puis le volume de la TV. Le son capturé est celui de la **sortie par défaut** de Windows : un logiciel qui joue sur une autre sortie, ou en mode exclusif, n'est pas transmis. Mettez aussi l'application TV à jour (les anciennes versions n'ont pas le son). |
| Plus aucun son quand *Couper le son du PC* est activé | Sur certains PC (volume géré par la carte son elle-même), couper le son coupe aussi la capture : désactivez cette option et baissez plutôt le volume des haut-parleurs du PC. |
| Son haché ou en retard | Préférez l'Ethernet ou le Wi-Fi 5 GHz. Le mode debug (lignes *Son*) montre le tampon et les coupures de la TV. Une barre de son en Bluetooth ajoute toujours du retard. |
| Manette non reconnue par le PC | Elle doit d'abord fonctionner sur la TV (appariée dans ses paramètres). Le mode debug liste les manettes vues. Si le pilote ViGEmBus n'a pas pu s'installer, une notification l'indique : relancez TVScreenPlus, ou installez [ViGEmBus 1.22](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0) à la main. Au-delà de 4 manettes, les suivantes restent sur la TV. |
| La manette pilote la TV au lieu du PC | Activez *Manettes* dans les paramètres de la TV et sur l'icône du PC, et mettez les deux applications à jour. |
| Pas de vibrations | La manette et la version d'Android de la TV doivent permettre les vibrations à distance (Android 12+ conseillé). Toutes les manettes Bluetooth ne le permettent pas. |
| Autre souci | Clic droit › *Ouvrir le journal* (`C:\ProgramData\TVScreenPlus\log.txt`) et joignez-le à une [issue](https://github.com/AbelSlawd/TVScreenPlus/issues). Il contient aussi les informations de la TV (lignes `TV:`) et les statistiques de la diffusion (lignes `Stats:`). |

## Désinstallation
- **PC** : *Paramètres › Applications › TVScreenPlus › Désinstaller*. Le pilote d'écran virtuel, la règle du pare-feu et le démarrage automatique sont aussi supprimés, ainsi que le pilote de manettes ViGEmBus s'il a été installé par TVScreenPlus (s'il était déjà là pour un autre logiciel, il est conservé).
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
| Son | Capture WASAPI *loopback* de la sortie par défaut de Windows, converti en PCM 16 bits stéréo (mixage des voies surround, 96/192 kHz ramenés à 48/44,1 kHz) et envoyé sans compression par paquets de 10 à 20 ms dans la même connexion que l'image. Sur la TV : AudioTrack en mode *low-latency* (Android 8+), tampon adaptatif (environ 30 ms au départ), le son en retard est jeté plutôt que joué tard |
| Manettes | La TV envoie l'état de chaque manette (boutons, sticks, gâchettes, croix) à chaque changement. Le PC crée une manette Xbox 360 virtuelle par manette avec le pilote [ViGEmBus](https://github.com/nefarius/ViGEmBus), piloté directement (sans DLL). Les vibrations demandées par le jeu sont renvoyées à la TV |

**Ports réseau** : UDP 47801 (découverte), TCP 47800 (flux). La règle de pare-feu est ajoutée automatiquement.

<details>
<summary>Protocole</summary>

- **Découverte** : la TV diffuse `TVSP?` en UDP et le PC répond `TVSP!\nname=…\nbusy=0|1\nver=…`.
- **Connexion** : la TV envoie `TVSP1;v=2;name=…;w=…;h=…;hz=…;hevc=<hauteur max>;avc=<hauteur max>;…`.
- **Flux** : paquets `[u32 taille][u8 flags][données]`. Flags : `0x80` remplissage (test de débit), `0x40` ping (la TV répond `ACK`), `0x20` configuration, `0x10` message d'état, sinon une image vidéo Annex-B (`1` = image clé).
- **Retour TV → PC** : `STAT;fps=…;kbps=…;drop=…;dec=…;decmax=…;wait=…` toutes les 2 secondes (`dec`/`decmax` : latence du décodeur en ms, `wait` : % du temps où son entrée est pleine), `LOG;…` (diagnostic écrit dans le journal du PC), et `ACK` en réponse aux pings envoyés chaque seconde pour mesurer la latence.
- **Champs supplémentaires de la connexion** : `lang`, `tested`, `lat` (latence de décodage en ms), `net`/`band`/`link` (réseau), `pref` (priorité), `maxh` (résolution max), `audio=0|1` (la TV joue le son), `pads=0|1` (la TV envoie ses manettes).
- **Fonctions optionnelles** : le PC ne les utilise que si la TV les annonce dans sa ligne de connexion, et confirme ce qu'il accepte dans son paquet de configuration (`…;audio=0|1;pads=0|1`). La TV indique aussi `audioudp=<port>` : le PC lui envoie alors le son en datagrammes UDP de 5 ms (même contenu que les paquets 0x08), sinon dans le flux TCP. `avsync=0` (mode jeu) interdit de retarder l'image ; `mode=fluid|game` indique le mode actif, et la TV demande un changement avec la ligne `MODE;fluid|game` (touches ▲ ▼). `ts=1` : chaque image vidéo porte le drapeau `0x04` et commence par son heure de capture (u32, ms depuis 1970 modulo 2³¹) ; les pings portent l'heure du PC ; la TV renvoie `e2e`/`e2emax` (capture → écran) dans `STAT`. `cursor=1` : la TV dessine le curseur ; le PC envoie sa position (`[3][visible][u16 x][u16 y][u32 heure]`, en UDP) et sa forme (`[4][u16 l][u16 h][u16 point chaud x][u16 y][BGRA]`, paquet 0x08). Une ancienne application TV ou un ancien programme PC continuent donc de fonctionner (sans son ni manettes).
- **Paquets `0x08` (extension)** : le premier octet donne le type. Une ancienne TV ne les reçoit jamais (ils seraient pris pour de la vidéo).
  - `1` = son : `[1][format 1 = PCM 16 bits LE][voies][0][fréquence u32][horodatage PC en ms u32][échantillons]`, 10 à 20 ms par paquet.
  - `2` = vibrations : `[2][emplacement 0-3][moteur gros 0-255][moteur petit 0-255]`.
- **Manettes, TV → PC** : `PADON;<emplacement>;<nom>` (manette apparue), `PADOFF;<emplacement>`, et à chaque changement `PAD;<emplacement>;<boutons hexa, bits XInput>;<gâchette G 0-255>;<gâchette D>;<stick G x>;<stick G y>;<stick D x>;<stick D y>` (sticks de -32768 à 32767, y vers le haut).
- **Statistiques du son** : `STAT;…;alat=<tampon TV en ms>;aund=<coupures>;adrop=<paquets jetés>;ajit=<gigue en ms>;aout=<délai écriture → haut-parleur en ms>;abuf=<tampon visé>;aext=<retard ajouté au son>;vdel=<retard ajouté à l'image>;avoff=<réglage utilisateur>`.
- **Synchronisation** : le paquet de configuration du PC indique `vpc` (retard de l'image sur le son au départ du PC, en ms) et `apc` (délai de capture du son).
</details>

## Compiler soi-même
```bash
# Programme PC (.NET Framework 4.8, inclus dans Windows)
dotnet build windows/TVScreenPlus -c Release

# Application TV
cd android && ./gradlew assembleRelease
```
Sans `ffmpeg.exe` ni pilote à côté du programme, ils sont téléchargés au premier lancement.

**Intégration continue** (`.github/workflows/build.yml`) : chaque push compile les deux applications, ajoute ffmpeg, le pilote d'écran et l'installateur ViGEmBus (signature vérifiée), puis publie une Release `v1.0.N`.
Pour que les mises à jour de l'APK s'installent par-dessus les précédentes, la CI signe l'APK avec une clé fixe fournie par deux secrets du dépôt : `ANDROID_KEYSTORE_BASE64` (keystore en base64, alias `tvsp`) et `ANDROID_KEYSTORE_PASSWORD`.

## Crédits
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (licence MIT) : écran virtuel Windows.
- [FFmpeg](https://ffmpeg.org/), version [BtbN](https://github.com/BtbN/FFmpeg-Builds) (licence GPL) : capture et encodage.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) 1.22 (licence BSD 3 clauses, © 2016-2020 Nefarius Software Solutions e.U.) : manettes Xbox virtuelles. Le programme d'installation signé est inclus tel quel dans `TVScreenPlus-PC.zip`, avec sa licence (`ViGEmBus_LICENSE.txt`). Les requêtes envoyées au pilote suivent celles de [ViGEmClient](https://github.com/nefarius/ViGEmClient) (licence MIT). Le projet ViGEm n'est plus développé, mais le pilote reste signé et fonctionne sous Windows 10 et 11.
