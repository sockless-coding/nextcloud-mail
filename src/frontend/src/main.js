import '@nextcloud/dialogs/style.css'
import { createApp } from 'vue'
import App from './App.vue'

// AppAPI's embedded page template is an empty <div id="content">.
createApp(App).mount('#content')
