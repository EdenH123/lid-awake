#!/bin/sh
# The running app serves web/index.html as the phone page (run after build.sh).
cd "$(dirname "$0")" || exit 1
node page_check.js "$(cd .. && pwd)/web/index.html"
