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
