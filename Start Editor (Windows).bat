@echo off
rem Launcher lives at the top level; everything else sits in editor\.
cd /d "%~dp0editor"
py eceditor.py
pause
