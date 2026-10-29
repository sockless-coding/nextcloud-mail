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
	inbox: t('sockless_mail', 'Inbox'),
	drafts: t('sockless_mail', 'Drafts'),
	sent: t('sockless_mail', 'Sent'),
	junk: t('sockless_mail', 'Junk'),
	trash: t('sockless_mail', 'Trash'),
	archive: t('sockless_mail', 'Archive'),
}
