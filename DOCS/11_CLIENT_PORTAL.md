# Client Portal

The Client Portal is a secure, separate login for an agent's own clients — distinct from the agent's own IPRO login. It is a Platinum/Broker package feature by default; Super Admin can change which packages include it under **Packages**.

## Invite a Client to the Portal

1. Open the client's profile from **Clients**.
2. Under **Client Portal**, click **Invite to Portal**.
3. The client receives an email with a link to set their own password and activate their account.
4. The status updates to "Invited, pending activation" until they complete setup, then to "Active since &lt;date&gt;".

If a client loses their invite email, click **Resend Invite** — this generates a fresh activation link and clears any half-finished setup.

## Revoke Portal Access

Click **Revoke Access** on the client's profile at any time. This immediately clears their portal password, so they can no longer log in until re-invited.

## Portal Messages

1. Select **Portal Messages** in the Agent Portal to see every client conversation, most recent first, with an unread badge.
2. Click a conversation to open the full thread and reply.

Clients see the same thread from their side of the portal — it is one continuous conversation per client, not a ticket system.

## Portal Requests (Appointments)

Clients can request an appointment from their portal, optionally with a preferred date and notes. These appear under **Portal Requests** in the Agent Portal.

1. Filter by Pending / Scheduled / Declined / All.
2. Click **Mark Scheduled** or **Decline** to respond.
3. Marking a request Scheduled does not automatically create a calendar entry — add the actual appointment through the existing **Calendar**/Follow-up tools yourself.

Clients also see any of their own upcoming follow-ups (read-only) on their Appointments page.

## Documents

Both the agent and the client can upload documents into a shared folder per client:

- Agents upload from the client's profile page, under **Portal Documents**.
- Clients upload from their own **Documents** page in the portal.

Both sides can download anything uploaded by either party. Files are capped at 20 MB per upload.

An agent can delete any document for their own clients. A client can only delete documents they themselves uploaded — not ones the agent shared with them, since those may be official records the agent needs kept.

### Important Behavior

- Allowed file types: PDF, Word (.doc/.docx), Excel (.xls/.xlsx), images (JPG/PNG/GIF/WebP), TXT, CSV. Any other file type — including executables, scripts, or HTML — is rejected at upload.
- Each upload's actual content is checked against its extension (a renamed file with mismatched contents is rejected), and downloads always force a file-save dialog rather than opening in the browser.
- Uploaded documents are stored in a private, non-public storage location — the only way to retrieve a file is through the authenticated Download links in the portal, never a bare public URL.
- Antivirus/malware scanning of uploaded files is **not** performed today.

## My Information

Clients can update their own contact details (name, phone, address) directly from their portal's **My Information** page — changes save immediately to the same client record visible in your CRM.

## Invoices

Clients see all estimates/invoices you've sent them under their portal's **Invoices** tab, each linking to the same invoice page used for the standalone signed links.
