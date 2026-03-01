cd /d D:\sourceProject\repos\lightmap-uv-tool
git rm -f tmp_push.bat 2>nul
if exist tmp_push.bat del tmp_push.bat
echo tmp_push.bat>> .gitignore
git add -A
git commit -m "remove tmp_push.bat, add to gitignore"
git push
