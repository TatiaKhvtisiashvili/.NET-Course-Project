console.log("Client-side script loaded!");

document.addEventListener('DOMContentLoaded', () => {
    const heading = document.querySelector('h1');
    if (heading) {
        heading.addEventListener('click', function () {
            alert('You clicked the H1 heading! The Web Server is responding.');
        });
    }
});