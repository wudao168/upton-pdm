import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/app.css'
import ServerConnectionSettings from './components/ServerConnectionSettings.vue'

createApp(ServerConnectionSettings).use(ElementPlus, { locale: zhCn }).mount('#app')
