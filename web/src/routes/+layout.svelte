<script lang="ts">
	import '../app.css';
	import favicon from '$lib/assets/favicon.svg';

	let { children } = $props();
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
	<title>vigil</title>
	<meta name="robots" content="noindex" />
</svelte:head>

{@render children()}
