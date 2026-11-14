**Nouveau en 1.2.3 : nouveau logo** (application TV)
- Nouvelle icône et nouvelle bannière de l'application TV, avec le nom TVScreenPlus. L'icône est dessinée pour l'écran d'accueil de Google TV, qui affiche les applications dans des ronds.
- Aucun changement de fonctionnement : la version du PC est identique à la 1.2.2.
- Le projet est encore en phase de test : un bug, une TV ou une manette qui ne fonctionne pas ? Signalez-le dans la section [Issues](https://github.com/AbelSlawd/TVScreenPlus/issues).

**Nouveau en 1.2.2 : toutes les manettes, page Manettes, menu rapide, réveil du PC** — mettez à jour le PC **et** l'application TV.

Manettes
- **Manettes Nintendo de la Switch** : manette Pro et Joy-Con. Deux Joy-Con (gauche + droit) forment une seule manette ; un Joy-Con seul se tient à l'horizontale. Les boutons A B X Y sont placés comme sur une manette Xbox (bouton du bas = A), ou comme sur une Switch (réglable). Capture sert de bouton Guide (HOME est réservé par la TV).
- **Manettes de la Switch 2** (Joy-Con 2, manette Pro 2, manette GameCube) : activez *Manettes Switch 2* dans la page Manettes, puis appuyez sur le bouton SYNC de la manette. Vibrations et voyants de joueur compris.
- **Page Manettes** (bouton *Manettes* en haut de l'accueil, ou pastilles des manettes en bas à gauche) : dessin en direct de chaque manette, numéro de joueur, boutons façon Xbox ou Nintendo, force des vibrations, zone morte des sticks, séparer / associer les Joy-Con (pour jouer à deux), test des boutons, faire vibrer pour reconnaître une manette.
- Limites de certaines TV (TCL testée), indiquées dans la page Manettes : pas de vibrations pour les manettes Nintendo de la Switch 1, et pas de boutons SL / SR pour les Joy-Con de la Switch 1 (un Joy-Con seul utilise alors L / ZL ou R / ZR comme gâchettes du haut).

Diffusion
- **Menu rapide** pendant la diffusion : **appui long sur OK** (ou touche Menu) → mode fluide / jeu, synchro du son, statistiques, déconnecter.
- **Taille de l'image** (Paramètres de la TV, 100 à 90 %) pour les TV qui coupent les bords de l'image.
- **Réveil du PC à distance** : un PC déjà vu qui ne répond plus apparaît grisé dans la liste ; OK le réveille par le réseau (Wake-on-LAN, à autoriser sur le PC, de préférence en Ethernet).

Corrections (revue complète du code)
- TV : changement de mode fluide / jeu plus sûr (décodeur), son plus robuste, reconnexion automatique si la première image n'arrive jamais, mise à jour TV ratée correctement annulée.
- PC : débrancher une manette ne coupe plus les vibrations des autres ; une image corrompue possible avec l'encodeur ffmpeg sur un bureau immobile ; « Couper le son du PC » suit le changement de sortie audio ; la désinstallation ne supprime plus que le dossier d'installation ; règle du pare-feu réessayée si elle a échoué.

**Nouveau en 1.2.1 : qualité du son** (application TV)
- Nouveau réglage *Paramètres de la TV › Sortie du son* :
  - **Rapide** (par défaut, comme en 1.2.0) : latence minimale. Sur certaines TV (TCL/MediaTek), ce chemin rapide envoie le son directement aux haut-parleurs, sans les effets sonores de la TV (Dolby, égaliseur) : c'est pourquoi le son paraissait moins riche depuis la 1.1.4.
  - **Stable** : comme Rapide, avec plus de marge ; plus de micro-coupures (~30 ms de latence en plus).
  - **Qualité TV** : le son passe par les effets sonores de la TV (Dolby…), ~200 ms. Idéal pour les films et la musique en mode fluide, où l'image attend le son. En mode jeu, le son arrive en retard. Le réglage de synchro ◀ ▶ est mémorisé à part pour ce choix.

**Nouveau en 1.2.0 : sécurité** — ⚠️ mettez à jour le PC **et** l'application TV (l'ancienne application TV est refusée et vous demande de la mettre à jour).
- **Jumelage des TV** : seules les TV acceptées sur le PC reçoivent son écran. À la première connexion, la TV affiche un code à 6 chiffres et le PC ouvre une fenêtre avec le même code : cliquez *Autoriser*. Ensuite, la TV se reconnecte seule, comme avant. Menu de l'icône → *Oublier les TV autorisées* pour tout annuler.
- Le PC ne répond qu'aux appareils du réseau local, et une TV inconnue ne peut plus couper la diffusion en cours.
- Règle du pare-feu limitée aux deux ports de TVScreenPlus et au réseau local.
- Plus aucun téléchargement exécuté sans vérification : ffmpeg et les pilotes viennent uniquement du zip ; les mises à jour viennent uniquement des releases du projet et sont décompressées dans *Program Files*.
- Dossier de données réservé aux administrateurs en écriture, réglages (clés de jumelage) illisibles pour les autres comptes.
- TV : la liste des PC se met à jour correctement après « Retour » (le PC restait parfois introuvable plusieurs minutes).
- TV : nouvelle interface (cartes des PC, focus plus visible, paramètres plus lisibles, statistiques dans un cadre arrondi).

**Nouveau en 1.1.6**
- WiFi : image aussi nette qu'en Ethernet. Le débit ne baisse plus définitivement après chaque petit à-coup du WiFi et remonte quand le réseau va bien (mesuré : 63 Mb/s au lieu de 30 Mb/s, ~45-50 ms de latence, aucune perte d'image ni coupure de son).
- WiFi : un à-coup ne fait plus rejouer une demi-seconde d'image en accéléré ; l'image revient au direct plus vite.
- Connexion à la TV ~0,7 s plus rapide.
- Plus de coupure du son quand on passe du mode jeu au mode fluide (TV avec sortie audio directe).
- Astuce : sur certaines Google TV, les services Google balaient le WiFi toutes les 30 minutes (à-coup d'environ 0,5 s). Désactiver la localisation de la TV le supprime.

**Nouveau en 1.1.5**
- Correction : avec les cartes NVIDIA, l'image pouvait se figer 5 secondes de temps en temps (1.1.3 et 1.1.4). Le réglage d'encodage en cause est retiré ; la latence d'image revient à celle de la 1.1.2 (quelques ms de plus), le son reste aussi rapide qu'en 1.1.4.

**Nouveau en 1.1.4**
- Son beaucoup plus rapide sur la TV : il passe par la sortie audio directe d'Android (AAudio) quand la TV la propose. Mesuré sur une TV TCL : ~45 ms au lieu de ~200 ms, le son est maintenant presque synchrone avec l'image. Si la TV refuse cette sortie, l'ancien chemin est utilisé automatiquement.
- Mode fluide bien plus réactif : comme le son n'est plus en retard, l'image n'a presque plus besoin d'être retardée pour rester synchronisée (~45 ms au lieu de ~140 ms).

**Encodeur vidéo TVScreenPlus** (depuis 1.1, sans ffmpeg) — 60 images/s régulières et latence réduite, y compris en mode jeu.
✅ Testé et validé sur carte **NVIDIA** (GeForce RTX 5080, 4K 60 i/s).
⚠️ Les autres configurations (AMD, Intel, autres générations NVIDIA, PC portables à deux GPU) restent à tester. Si l'encodeur ne fonctionne pas ou est trop lent, l'encodeur ffmpeg de la version 1.0 prend le relais automatiquement ; il peut aussi être choisi à la main : icône TVScreenPlus → *Encodeur vidéo* → *ffmpeg*.
