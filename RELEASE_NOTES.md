**Nouveau en 1.1.3**
- Latence réduite de 5 à 10 ms avec les cartes NVIDIA : l'encodeur rend chaque image dès qu'elle est prête au lieu d'attendre la suivante (encodage ~3 ms au lieu de ~14 ms en 4K).
- La TV mesure maintenant vraiment sa latence (capture → écran), y compris sur les TV MediaTek qui ne signalent pas l'affichage des images ; mesuré sur TCL : ~30-45 ms en mode jeu.

**Encodeur vidéo TVScreenPlus** (depuis 1.1, sans ffmpeg) — 60 images/s régulières et latence réduite, y compris en mode jeu.
✅ Testé et validé sur carte **NVIDIA** (GeForce RTX 5080, 4K 60 i/s).
⚠️ Les autres configurations (AMD, Intel, autres générations NVIDIA, PC portables à deux GPU) restent à tester. Si l'encodeur ne fonctionne pas ou est trop lent, l'encodeur ffmpeg de la version 1.0 prend le relais automatiquement ; il peut aussi être choisi à la main : icône TVScreenPlus → *Encodeur vidéo* → *ffmpeg*.
