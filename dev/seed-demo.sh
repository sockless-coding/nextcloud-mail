#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
#
# Fills two dev GreenMail mailboxes with believable mail for screenshots (tests/e2e/screenshots.mjs):
# alice@example.test as a work account (a conversation, an invitation, an attachment) and
# bob@example.test as a personal one (a newsletter, notes from friends). All names and
# addresses are made up; dates are spread over the last two days.
set -euo pipefail

SMTP="smtp://127.0.0.1:3025"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
n=0

ago() { date -R -d "-$1 minutes"; }
stamp() { date -u -d "@$1" +%Y%m%dT%H%M%SZ; }

# send <to> <file>: via stdin, so curl gets no file path to misread on Windows.
send() {
    curl -s --fail --url "$SMTP" --mail-from "demo@example.test" --mail-rcpt "$1" --upload-file - < "$2"
}

# plain <to> <from> <subject> <minutes ago> <body> [message-id] [in-reply-to] [To header]
plain() {
    n=$((n + 1))
    {
        echo "From: $2"
        echo "To: ${8:-$1}"
        echo "Subject: $3"
        echo "Date: $(ago "$4")"
        echo "Message-ID: <${6:-demo-$n-$RANDOM}@example.test>"
        if [ -n "${7:-}" ]; then
            echo "In-Reply-To: <$7@example.test>"
            echo "References: <$7@example.test>"
        fi
        echo "MIME-Version: 1.0"
        echo "Content-Type: text/plain; charset=utf-8"
        echo
        printf '%b\n' "$5"
    } > "$TMP/m.eml"
    send "$1" "$TMP/m.eml"
}

WORK="Alice Martin <alice@example.test>"
HOME_="Bob Lindqvist <bob@example.test>"

# The planning thread goes to the whole team each time.
TEAM="Alice Martin <alice@example.test>, Daniel Okafor <daniel.okafor@northwind.example>, Priya Raman <priya.raman@northwind.example>"

# --- Work: alice@example.test -------------------------------------------------------------

plain alice@example.test "Nora Ellis <nora.ellis@northwind.example>" "Weekly status: website relaunch" 2830 \
    "Hi Alice,\n\nQuick summary for this week:\n\n- Navigation and search are done\n- Content migration is at 80 %\n- Accessibility review is booked for Thursday\n\nNo blockers right now.\n\nNora"

plain alice@example.test "Daniel Okafor <daniel.okafor@northwind.example>" "Q4 planning: draft agenda" 1460 \
    "Hi all,\n\nHere's a first draft for Friday's planning session:\n\n1. Review of Q3 results\n2. Priorities for Q4\n3. Hiring plan\n4. Budget\n\nAdd anything I missed.\n\nDaniel" \
    q4-agenda "" "$TEAM"
plain alice@example.test "Priya Raman <priya.raman@northwind.example>" "Re: Q4 planning: draft agenda" 1380 \
    "Could we add 15 minutes on the support backlog? It keeps coming up.\n\nPriya\n\n> Here's a first draft for Friday's planning session" \
    q4-priya q4-agenda "$TEAM"
plain alice@example.test "Daniel Okafor <daniel.okafor@northwind.example>" "Re: Q4 planning: draft agenda" 1290 \
    "Good point, added as item 3. Updated agenda:\n\n1. Review of Q3 results\n2. Priorities for Q4\n3. Support backlog\n4. Hiring plan\n5. Budget\n\nSee you Friday at 10.\n\nDaniel" \
    q4-daniel q4-priya "$TEAM"

# An invitation with a text/calendar part (shown as an invitation card).
start="$(date -d 'tomorrow 10:00' +%s)"
cat > "$TMP/invite.eml" <<EOF
From: Daniel Okafor <daniel.okafor@northwind.example>
To: $WORK
Subject: Invitation: Q4 planning session
Date: $(ago 1200)
Message-ID: <demo-invite-$RANDOM@example.test>
MIME-Version: 1.0
Content-Type: multipart/alternative; boundary="inv"

--inv
Content-Type: text/plain; charset=utf-8

You are invited to the Q4 planning session, tomorrow 10:00-11:30, Room 4.

--inv
Content-Type: text/calendar; charset=utf-8; method=REQUEST

BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Sockless Mail//demo//EN
METHOD:REQUEST
BEGIN:VEVENT
UID:q4-planning-demo@northwind.example
DTSTAMP:$(stamp "$(date +%s)")
DTSTART:$(stamp "$start")
DTEND:$(stamp $((start + 5400)))
SUMMARY:Q4 planning session
LOCATION:Room 4
ORGANIZER;CN=Daniel Okafor:mailto:daniel.okafor@northwind.example
ATTENDEE;CN=Alice Martin;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:alice@example.test
END:VEVENT
END:VCALENDAR
--inv--
EOF
send alice@example.test "$TMP/invite.eml"

PDF="$(printf '%%PDF-1.4\n%% Invoice 2026-118, Sockless Mail demo\n' | base64)"
cat > "$TMP/invoice.eml" <<EOF
From: Fabrikam Accounts <billing@fabrikam.example>
To: $WORK
Subject: Invoice 2026-118 for October
Date: $(ago 640)
Message-ID: <demo-invoice-$RANDOM@example.test>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="inv"

--inv
Content-Type: text/plain; charset=utf-8

Hello Alice,

Please find attached invoice 2026-118 for October. Payment is due within 30 days.

Kind regards,
Fabrikam Accounts

--inv
Content-Type: application/pdf; name="invoice-2026-118.pdf"
Content-Disposition: attachment; filename="invoice-2026-118.pdf"
Content-Transfer-Encoding: base64

$PDF
--inv--
EOF
send alice@example.test "$TMP/invoice.eml"

plain alice@example.test "Liam Chen <liam.chen@northwind.example>" "Design review notes" 95 \
    "Hi Alice,\n\nThanks for the review today. The notes:\n\n- Keep the compact list as the default\n- Move the account colours into the rail\n- Ship dark mode with the next release\n\nI'll send updated mockups on Monday.\n\nLiam"

plain alice@example.test "Priya Raman <priya.raman@northwind.example>" "Lunch on Thursday?" 22 \
    "The new place around the corner has opened. Thursday at 12:30?\n\nPriya"

# --- Personal: bob@example.test -----------------------------------------------------------

plain bob@example.test "Emma Lindqvist <emma.lindqvist@example.net>" "Photos from the weekend" 2600 \
    "Hi Bob,\n\nI finally sorted the photos from the lake. I'll share the album with you tonight.\n\nHugs,\nEmma"

plain bob@example.test "Skyway Travel <bookings@skyway.example>" "Your trip to Lisbon: booking confirmed" 1700 \
    "Booking reference: QX7P2L\n\nOutbound: 14 Nov, 08:40, Stockholm to Lisbon\nReturn: 18 Nov, 17:15, Lisbon to Stockholm\n\nHave a great trip!"

cat > "$TMP/news.eml" <<EOF
From: The Garden Journal <hello@gardenjournal.example>
To: $HOME_
Subject: November in the garden: five things to do now
Date: $(ago 300)
Message-ID: <demo-news-$RANDOM@example.test>
MIME-Version: 1.0
Content-Type: multipart/alternative; boundary="news"

--news
Content-Type: text/plain; charset=utf-8

November in the garden: plant bulbs, protect tender plants, clean tools, collect leaves, plan next year.

--news
Content-Type: text/html; charset=utf-8

<html><body style="margin:0;background:#f3f1ea;font-family:Georgia,serif;color:#2f3b2f">
<table width="100%" cellpadding="0" cellspacing="0"><tr><td align="center">
<table width="560" cellpadding="24" cellspacing="0" style="background:#ffffff">
<tr><td style="background:#3f6b45;color:#ffffff">
<h1 style="margin:0;font-size:26px">The Garden Journal</h1>
<p style="margin:4px 0 0">November issue</p></td></tr>
<tr><td>
<h2 style="font-size:20px;margin-top:0">Five things to do now</h2>
<ol style="line-height:1.6">
<li><b>Plant spring bulbs</b> before the ground freezes.</li>
<li><b>Protect tender plants</b> with fleece on cold nights.</li>
<li><b>Clean and oil your tools</b> before storing them.</li>
<li><b>Collect fallen leaves</b> for leaf mould.</li>
<li><b>Plan next year's beds</b> while this year is fresh in mind.</li>
</ol>
<img src="https://track.gardenjournal.example/open.gif" width="1" height="1" alt="">
<p style="font-size:13px;color:#6b746b">You receive this because you subscribed at gardenjournal.example.</p>
</td></tr></table></td></tr></table>
</body></html>
--news--
EOF
send bob@example.test "$TMP/news.eml"

plain bob@example.test "Jonas Berg <jonas.berg@example.net>" "Board game night on Saturday" 45 \
    "Hey,\n\nWe're doing board games at our place on Saturday from 7. Bring snacks if you like, we have plenty of games.\n\nJonas"

echo "Seeded demo mail for alice@example.test (work) and bob@example.test (personal)."
