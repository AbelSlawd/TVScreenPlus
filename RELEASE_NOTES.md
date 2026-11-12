**Nouveau en 1.1.5**
- Correction : avec les cartes NVIDIA, l'image pouvait se figer 5 secondes de temps en temps (1.1.3 et 1.1.4). Le réglage d'encodage en cause est retiré ; la latence d'image revient à celle de la 1.1.2 (quelques ms de plus), le son reste aussi rapide qu'en 1.1.4.

**Nouveau en 1.1.4**
- Son beaucoup plus rapide sur la TV : il passe par la sortie audio directe d'Android (AAudio) quand la TV la propose. Mesuré sur une TV TCL : ~45 ms au lieu de ~200 ms, le son est maintenant presque synchrone avec l'image. Si la TV refuse cette sortie, l'ancien chemin est utilisé automatiquement.
- Mode fluide bien plus réactif : comme le son n'est plus en retard, l'image n'a presque plus besoin d'être retardée pour rester synchronisée (~45 ms au lieu de ~140 ms).

**Encodeur vidéo TVScreenPlus** (depuis 1.1, sans ffmpeg) — 60 images/s régulières et latence réduite, y compris en mode jeu.
✅ Testé et validé sur carte **NVIDIA** (GeForce RTX 5080, 4K 60 i/s).
⚠️ Les autres configurations (AMD, Intel, autres générations NVIDIA, PC portables à deux GPU) restent à tester. Si l'encodeur ne fonctionne pas ou est trop lent, l'encodeur ffmpeg de la version 1.0 prend le relais automatiquement ; il peut aussi être choisi à la main : icône TVScreenPlus → *Encodeur vidéo* → *ffmpeg*.
