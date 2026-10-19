#!/bin/sh
# The phone page words "iPhone"/"Face ID" only through word() or data-w, never hard-coded.
cd "$(dirname "$0")" || exit 1
node words_check.js ../web/index.html
