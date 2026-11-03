import { mdiAlertOctagonOutline, mdiArchiveOutline, mdiDeleteOutline, mdiEmailMultipleOutline, mdiFileDocumentEditOutline, mdiInbox, mdiSendOutline, mdiStarOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'

/** Icons of special-use folders (FolderDto.specialUse), shared by the folder tree and the unified folders. */
export const USE_ICONS = {
	inbox: mdiInbox,
	drafts: mdiFileDocumentEditOutline,
	sent: mdiSendOutline,
	archive: mdiArchiveOutline,
	junk: mdiAlertOctagonOutline,
	trash: mdiDeleteOutline,
	all: mdiEmailMultipleOutline,
	flagged: mdiStarOutline,
}

export const USE_NAMES = {
	get inbox() { return t('sockless_mail', 'Inbox') },
	get drafts() { return t('sockless_mail', 'Drafts') },
	get sent() { return t('sockless_mail', 'Sent') },
	get junk() { return t('sockless_mail', 'Junk') },
	get trash() { return t('sockless_mail', 'Trash') },
	get archive() { return t('sockless_mail', 'Archive') },
}

/**
 * A folder's name as shown: special folders get their translated name when the server uses the
 * standard English one ("INBOX", "Sent"); other names are the user's own.
 */
export function folderDisplayName(folder) {
	return (folder.specialUse && folder.name.toLowerCase() === folder.specialUse ? USE_NAMES[folder.specialUse] : null) ?? folder.name
}
