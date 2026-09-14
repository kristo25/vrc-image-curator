@echo off
setlocal
cd /d "%~dp0"
set LOG=%~dp0repo-rename-log.txt
> "%LOG%" echo === rename the GitHub repository ===
>>"%LOG%" echo --- is the GitHub CLI available and signed in? ---
gh --version >>"%LOG%" 2>&1
if errorlevel 1 (
  >>"%LOG%" echo GH_CLI=missing
  goto :manual
)
gh auth status >>"%LOG%" 2>&1
if errorlevel 1 (
  >>"%LOG%" echo GH_AUTH=no
  goto :manual
)
>>"%LOG%" echo GH_AUTH=yes
>>"%LOG%" echo.
>>"%LOG%" echo --- renaming vrc-image-curator to vrc-pic-sorter ---
gh repo rename vrc-pic-sorter --repo kristo25/vrc-image-curator --yes >>"%LOG%" 2>&1
if errorlevel 1 (
  >>"%LOG%" echo RENAME=failed
  goto :manual
)
>>"%LOG%" echo RENAME=ok
git remote set-url origin https://github.com/kristo25/vrc-pic-sorter.git >>"%LOG%" 2>&1
>>"%LOG%" echo --- remote now ---
git remote -v >>"%LOG%" 2>&1
>>"%LOG%" echo --- does the new remote answer? ---
git ls-remote --heads origin main >>"%LOG%" 2>&1
>>"%LOG%" echo LSREMOTE_EXIT=%ERRORLEVEL%
goto :end

:manual
>>"%LOG%" echo.
>>"%LOG%" echo Rename it in the browser instead: repository Settings, General, Repository name.
>>"%LOG%" echo Then run: git remote set-url origin https://github.com/kristo25/vrc-pic-sorter.git

:end
echo.
echo Done. See repo-rename-log.txt
echo.
pause
endlocal
