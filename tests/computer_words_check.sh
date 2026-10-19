#!/bin/sh
# The phone page words "Mac" only through word("computer") or data-w, never hard-coded.
cd "$(dirname "$0")" || exit 1
node computer_words_check.js ../web/index.html
