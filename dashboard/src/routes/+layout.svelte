<script lang="ts">
	import '../app.css';
	import favicon from '$lib/assets/favicon.svg';

	let { children } = $props();
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
	<title>fancontrol</title>
	<meta name="robots" content="noindex" />
	<!-- Applied before first paint so a stored theme choice never flashes the other one. -->
	{@html `<script>try{var t=localStorage.getItem('theme');if(t==='light'||t==='dark')document.documentElement.dataset.theme=t}catch(e){}<\/script>`}
</svelte:head>

{@render children()}
