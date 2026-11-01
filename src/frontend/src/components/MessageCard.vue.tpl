<template>
	<div class="message-view__card">
		<header class="message-view__header">
				<!-- Who, to whom, when, and the actions on this message. -->
				<div class="message-view__meta">
					<NcAvatar :display-name="senderName"
						:url="bimiUrl(message)"
						is-no-user
						disable-menu
						:size="40" />
					<div class="message-view__people">
						<div class="message-view__sender">
							<strong>{{ senderName }}</strong>
							<span v-if="sender?.name" class="message-view__address">{{ " " }}&lt;{{ senderAddress }}&gt;</span>
						</div>
						<button class="message-view__recipients"
							:aria-expanded="String(details)"
							:title="details ? t('sockless_mail', 'Hide details') : t('sockless_mail', 'Show details')"
							@click="details = !details">
							<span class="message-view__recipients-text">
								{{ t('sockless_mail', 'To: {recipients}', { recipients: formatList(message.to) }) }}
								<template v-if="message.cc.length">
									· {{ t('sockless_mail', 'Cc: {recipients}', { recipients: formatList(message.cc) }) }}
								</template>
							</span>
							<NcIconSvgWrapper :path="details ? mdiChevronUp : mdiChevronDown" :size="16" />
						</button>
					</div>
					<div class="message-view__aside">
						<div class="message-view__actions" role="toolbar" :aria-label="t('sockless_mail', 'Message actions')">
							<button v-if="isDraft"
								class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Continue editing')"
								:title="t('sockless_mail', 'Continue editing')"
								@click="continueDraft">
								<NcIconSvgWrapper :path="mdiPencilOutline" :size="20" />
							</button>
							<button v-for="mode in REPLIES"
								:key="mode.id"
								class="message-view__icon-button"
								:aria-label="mode.label"
								:title="mode.label"
								@click="reply(mode.id)">
								<NcIconSvgWrapper :path="mode.icon" :size="20" />
							</button>
							<NcActions v-if="assistantMenu"
								class="message-view__assistant"
								:aria-label="t('sockless_mail', 'Assistant')"
								:title="t('sockless_mail', 'Assistant')">
								<template #icon>
									<AssistantIcon :size="20" />
								</template>
								<NcActionButton v-if="assistantState.summarize" close-after-click @click="summarize(where, message.id)">
									<template #icon>
										<NcIconSvgWrapper :path="mdiTextBoxOutline" />
									</template>
									{{ t('sockless_mail', 'Summarize') }}
								</NcActionButton>
								<NcActionButton v-if="canTranslate" close-after-click @click="translateMessage(where, message.id)">
									<template #icon>
										<NcIconSvgWrapper :path="mdiTranslate" />
									</template>
									{{ t('sockless_mail', 'Translate') }}
								</NcActionButton>
								<NcActionButton v-if="assistantState.checkScam" close-after-click @click="checkScam(where, message.id)">
									<template #icon>
										<NcIconSvgWrapper :path="mdiShieldSearch" />
									</template>
									{{ t('sockless_mail', 'Check for scams') }}
								</NcActionButton>
							</NcActions>
							<button class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Tags')"
								:title="t('sockless_mail', 'Tags')"
								@click="askTags([{ ...where, message }])">
								<NcIconSvgWrapper :path="mdiTagOutline" :size="20" />
							</button>
							<button v-if="!windowMode"
								class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Open in new window')"
								:title="t('sockless_mail', 'Open in new window')"
								@click="openMessageWindow(where, message.id)">
								<NcIconSvgWrapper :path="mdiOpenInNew" :size="20" />
							</button>
							<!-- In a dark theme the mail is recoloured; its original white page is one click away. -->
							<button v-if="darkTheme"
								class="message-view__icon-button"
								:aria-pressed="String(original)"
								:aria-label="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
								:title="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
								@click="original = !original">
								<NcIconSvgWrapper :path="original ? mdiWeatherNight : mdiWhiteBalanceSunny" :size="20" />
							</button>
							<NcActions :aria-label="t('sockless_mail', 'More actions')">
								<NcActionButton close-after-click @click="toggleSeen(where, message)">
									<template #icon>
										<NcIconSvgWrapper :path="message.seen ? mdiEmailOutline : mdiEmailOpenOutline" />
									</template>
									{{ message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="toggleFlagged(where, message)">
									<template #icon>
										<NcIconSvgWrapper :path="message.flagged ? mdiFlagRemoveOutline : mdiFlagOutline" />
									</template>
									{{ message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="askMove(where, [message.id])">
									<template #icon>
										<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
									</template>
									{{ t('sockless_mail', 'Move…') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="remove(where, [message.id])">
									<template #icon>
										<NcIconSvgWrapper :path="mdiDeleteOutline" />
									</template>
									{{ t('sockless_mail', 'Delete') }}
								</NcActionButton>
								<NcActionButton v-if="tasksState.available"
									close-after-click
									@click="dialogs.createTask = { where, message }">
									<template #icon>
										<NcIconSvgWrapper :path="mdiClipboardCheckOutline" />
									</template>
									{{ t('sockless_mail', 'Create task…') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="createRuleFrom(where, message)">
									<template #icon>
										<NcIconSvgWrapper :path="mdiFilterPlusOutline" />
									</template>
									{{ t('sockless_mail', 'Create rule…') }}
								</NcActionButton>
								<NcActionButton v-if="message.from[0]"
									close-after-click
									@click="dialogs.addContact = { name: message.from[0].name, address: message.from[0].address }">
									<template #icon>
										<NcIconSvgWrapper :path="mdiAccountPlusOutline" />
									</template>
									{{ t('sockless_mail', 'Add sender to contacts') }}
								</NcActionButton>
								<NcActionSeparator />
								<NcActionButton close-after-click @click="rawView = 'headers'">
									<template #icon>
										<NcIconSvgWrapper :path="mdiCodeTags" />
									</template>
									{{ t('sockless_mail', 'View headers') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="rawView = 'source'">
									<template #icon>
										<NcIconSvgWrapper :path="mdiFileCodeOutline" />
									</template>
									{{ t('sockless_mail', 'View source') }}
								</NcActionButton>
								<NcActionLink :href="emlUrl(where, message.id)" download close-after-click>
									<template #icon>
										<NcIconSvgWrapper :path="mdiDownload" />
									</template>
									{{ t('sockless_mail', 'Download message (.eml)') }}
								</NcActionLink>
							</NcActions>
						</div>
						<div class="message-view__when">
							<NcIconSvgWrapper v-if="message.flagged"
								:path="mdiFlag"
								:size="16"
								inline
								class="message-view__flag"
								:name="t('sockless_mail', 'Flagged')" />
							<NcDateTime :timestamp="Date.parse(message.date)" :format="{ dateStyle: 'medium', timeStyle: 'short' }" :relative-time="false" />
						</div>
					</div>
				</div>

				<dl v-if="details" class="message-view__details">
					<template v-for="row in detailRows" :key="row.label">
						<dt>{{ row.label }}</dt>
						<dd>{{ row.value }}</dd>
					</template>
				</dl>
		</header>


		<section v-if="warning"
			class="message-view__warning"
			:class="`message-view__warning--${warning.level}`"
			role="alert">
			<NcIconSvgWrapper :path="mdiShieldAlertOutline" :size="22" class="message-view__warning-icon" />
			<div class="message-view__warning-text">
				<strong>{{ warningTitle(warning) }}</strong>
				<ul>
					<li v-for="(reason, index) in warning.reasons" :key="index">
						{{ reasonText(reason) }}
					</li>
				</ul>
				<p v-if="warning.asciiDomain" class="message-view__warning-ascii">
					{{ t('sockless_mail', 'The domain is spelled {ascii} in plain letters.', { ascii: warning.asciiDomain }) }}
				</p>
			</div>
			<div class="message-view__warning-buttons">
				<NcButton v-if="assistantState.checkScam && !scamChecked" variant="secondary" @click="checkScam(where, message.id)">
					<template #icon>
						<AssistantIcon :size="20" />
					</template>
					{{ t('sockless_mail', 'Ask the Assistant') }}
				</NcButton>
				<NcButton variant="secondary" :disabled="trusting" @click="trust(warning.registered)">
					{{ t('sockless_mail', 'Trust {domain}', { domain: warning.registered }) }}
				</NcButton>
			</div>
		</section>

		<div v-if="message.hasRemoteContent && !message.remoteContentAllowed" class="message-view__notice" role="status">
			<NcIconSvgWrapper :path="mdiImageOffOutline" :size="18" inline />
			<span>{{ t('sockless_mail', 'Remote images are blocked to protect your privacy.') }}</span>
			<button class="message-view__text-button" @click="openMessage(where, message.id, true)">
				{{ t('sockless_mail', 'Show images') }}
			</button>
		</div>

		<AssistantScamCheck :where="where" :message-id="message.id" />
		<AssistantSummary :where="where" :message-id="message.id" />
		<AssistantTranslation :where="where" :message-id="message.id" />

		<InvitationCard v-if="calendarPart"
			:key="`${message.id}/${calendarPart.partId}`"
			:where="where"
			:message-id="message.id"
			:part="calendarPart"
			:account-address="accountAddress" />

		<section v-if="message.attachments.length" class="message-view__attachments">
			<h3>
				{{ n('sockless_mail', '{count} attachment', '{count} attachments', message.attachments.length, { count: message.attachments.length }) }}
				<template v-if="message.attachments.length > 1 && attachmentsSize">
					· {{ formatFileSize(attachmentsSize) }}
				</template>
				<button v-if="message.attachments.length > 1"
					type="button"
					class="message-view__save-all"
					@click="saveToFiles(message.attachments)">
					{{ t('sockless_mail', 'Save all to Files') }}
				</button>
			</h3>
			<ul>
				<li v-for="attachment in message.attachments" :key="attachment.partId" class="message-view__attachment-card">
					<a :href="partUrl(where.accountId, where.folderId, message.id, attachment.partId, true)"
						class="message-view__attachment"
						:title="t('sockless_mail', 'Download {name}', { name: attachment.fileName })"
						download>
						<span class="message-view__file-type" :style="{ background: fileType(attachment).color }" aria-hidden="true">
							{{ fileType(attachment).label }}
						</span>
						<span class="message-view__file">
							<span class="message-view__attachment-name">{{ attachment.fileName }}</span>
							<span v-if="attachment.size" class="message-view__attachment-size">{{ formatFileSize(attachment.size) }}</span>
						</span>
						<NcIconSvgWrapper :path="mdiDownload" :size="18" class="message-view__download" />
					</a>
					<button type="button"
						class="message-view__save"
						:aria-label="t('sockless_mail', 'Save {name} to Files', { name: attachment.fileName })"
						:title="t('sockless_mail', 'Save to Files')"
						@click="saveToFiles([attachment])">
						<NcIconSvgWrapper :path="mdiFolderDownloadOutline" :size="18" />
					</button>
				</li>
			</ul>
		</section>

		<div class="message-view__body" :class="{ 'message-view__body--paper': darkTheme && original }">
			<MessageBody :key="`${message.id}/${message.remoteContentAllowed}/${original}`"
				:html="message.html"
				:original="original"
				@deceptive-link="askDeceptiveLink" />
		</div>

		<MessageSourceDialog v-if="rawView"
			:where="where"
			:message-id="message.id"
			:subject="message.subject"
			:kind="rawView"
			@close="rawView = null" />
		<DeceptiveLinkDialog v-if="deceptiveLink"
			:key="deceptiveLink.id"
			:link="deceptiveLink"
			@close="(id) => deceptiveLink?.id === id && (deceptiveLink = null)" />
	</div>
</template>
